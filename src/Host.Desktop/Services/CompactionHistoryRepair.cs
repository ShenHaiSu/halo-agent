using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop.Services;

public sealed record CompactionRepairReport
{
    public int OrphanStubCount { get; init; }
    public int EligibleOrphanStubCount { get; init; }
    public int LegacyTurnCount { get; init; }
    public int RepairedTurnCount { get; init; }
    public int SkippedCount { get; init; }
    public List<string> Warnings { get; init; } = new();
    public string? BackupPath { get; init; }
}

/// <summary>显式、可审计的 compact 历史修复；默认只读，不删除消息或旧 block。</summary>
public sealed class CompactionHistoryRepair
{
    private readonly WorkspaceSessionService _service;

    public CompactionHistoryRepair(WorkspaceSessionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public Task<CompactionRepairReport> AnalyzeAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    ) => RepairAsync(workspaceId, sessionId, false, ct);

    public async Task<CompactionRepairReport> RepairAsync(
        string workspaceId,
        string sessionId,
        bool apply,
        CancellationToken ct = default
    )
    {
        var store = new SessionHistoryStore(
            new SessionHistoryDb(_service.SessionRepository.Storage)
        );
        var messages = await store
            .GetMessagesAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        var turns = await store
            .GetTimelineSummaryAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        var stubs = messages
            .Where(message =>
                string.Equals(message.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
            )
            .ToList();
        var compactTurnIds = turns
            .Where(turn => string.Equals(turn.Kind, TurnKind.Compact, StringComparison.Ordinal))
            .Select(turn => turn.TurnId)
            .ToHashSet(StringComparer.Ordinal);
        var stubIds = stubs.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = stubs
            .Where(message => !compactTurnIds.Contains("cturn-" + message.Id))
            .ToList();
        var latest = messages.LastOrDefault();
        bool latestTerminalOrphan =
            orphans.Count > 0
            && latest != null
            && string.Equals(latest.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
            && string.Equals(latest.Id, orphans[^1].Id, StringComparison.Ordinal);
        int legacyTurnCount = turns.Count(turn =>
            string.Equals(turn.Kind, TurnKind.Turn, StringComparison.Ordinal)
            && turn.TurnId.StartsWith("uturn-", StringComparison.Ordinal)
            && stubIds.Contains(turn.TurnId.Substring(6))
        );
        var warnings = new List<string>();
        foreach (var orphan in orphans)
        {
            if (
                !latestTerminalOrphan
                || !string.Equals(orphan.Id, latest!.Id, StringComparison.Ordinal)
            )
                warnings.Add("ORPHAN_NOT_LATEST:" + orphan.Id);
        }
        var report = new CompactionRepairReport
        {
            OrphanStubCount = orphans.Count,
            EligibleOrphanStubCount = latestTerminalOrphan ? 1 : 0,
            LegacyTurnCount = legacyTurnCount,
            SkippedCount = latestTerminalOrphan ? Math.Max(0, orphans.Count - 1) : orphans.Count,
            Warnings = warnings,
        };
        if (!apply || !latestTerminalOrphan)
            return report;

        var appendLock = _service.GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            messages = await store
                .GetMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            turns = await store
                .GetTimelineSummaryAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            latest = messages.LastOrDefault();
            if (
                latest == null
                || !string.Equals(latest.Kind, MessageKind.CompactStub, StringComparison.Ordinal)
                || turns.Any(turn =>
                    string.Equals(turn.TurnId, "cturn-" + latest.Id, StringComparison.Ordinal)
                    && string.Equals(turn.Kind, TurnKind.Compact, StringComparison.Ordinal)
                )
            )
                return report with { SkippedCount = report.SkippedCount + 1 };

            var workspaceStore = new WorkspaceSessionStore(_service.SessionRepository.Storage);
            var backup = await workspaceStore
                .BackupWorkspaceAsync(workspaceId, ct)
                .ConfigureAwait(false);
            int removed = ReadRemovedCount(latest.MetaJson);
            var turn = new TurnSummary
            {
                TurnId = "cturn-" + latest.Id,
                Kind = TurnKind.Compact,
                IterEnd = 1,
                UserPreview = "上下文压缩",
                UserContent = latest.Content ?? string.Empty,
                RemovedMessages = removed,
                Blocks = 0,
                Tools = 0,
                Failed = 0,
                FinalBlockId = null,
                Status = TimelineTurnStatus.Completed,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            };
            await store
                .InsertCompactStubAsync(workspaceId, sessionId, latest, turn, ct)
                .ConfigureAwait(false);
            try
            {
                var meta = await _service
                    .GetSessionAsync(workspaceId, sessionId, ct)
                    .ConfigureAwait(false);
                if (meta != null)
                {
                    meta.MessageCount = messages.Count;
                    meta.CompactMode = CompactMode.Stub;
                    meta.UpdatedAtUtc = DateTime.UtcNow;
                    await _service
                        .RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("COMPACT_METADATA_WARNING:" + ex.GetType().Name);
            }
            try
            {
                HaloLogger.App.Info(
                    "[Compact] history.repair.applied ws="
                        + workspaceId
                        + " sess="
                        + sessionId
                        + " stub="
                        + latest.Id
                        + " turn="
                        + turn.TurnId
                        + " backup="
                        + backup.BackupPath
                );
            }
            catch { }
            return new CompactionRepairReport
            {
                OrphanStubCount = report.OrphanStubCount,
                EligibleOrphanStubCount = report.EligibleOrphanStubCount,
                LegacyTurnCount = report.LegacyTurnCount,
                RepairedTurnCount = 1,
                SkippedCount = report.SkippedCount,
                Warnings = warnings,
                BackupPath = backup.BackupPath,
            };
        }
        finally
        {
            appendLock.Release();
        }
    }

    private static int ReadRemovedCount(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
            return 0;
        try
        {
            return JsonSerializer
                    .Deserialize<CompactStubMeta>(metaJson, HaloJsonOptions.CompactCamelCase)
                    ?.RemovedCount ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
