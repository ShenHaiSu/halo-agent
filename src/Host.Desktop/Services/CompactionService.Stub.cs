using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

public sealed partial class CompactionService
{
    /// <summary>把最终摘要与完整覆盖统计一次性提交为 compact 桩。</summary>
    private async Task<CompactionResult> AppendStubAsync(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        CompactSnapshot snapshot,
        CompactionSummaryResult summaryResult,
        string? backupFile,
        CancellationToken ct
    )
    {
        var current = await LoadCurrentMessagesOrNull(service, workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        var retired = ResolveRetiredMessages(current, snapshot);
        int removed = retired?.Count ?? snapshot.OldCount;
        int concurrentExcluded =
            retired == null ? 0 : Math.Max(0, retired.Count - snapshot.OldCount);
        string? coveredFrom = retired?.FirstOrDefault()?.Id ?? snapshot.CoveredFromMessageId;
        string? coveredTo = retired?.LastOrDefault()?.Id ?? snapshot.CoveredToMessageId;
        var (fromSeq, toSeq) = await service
            .GetMessageSequenceRangeAsync(
                workspaceId,
                sessionId,
                coveredFrom ?? string.Empty,
                coveredTo ?? string.Empty,
                ct
            )
            .ConfigureAwait(false);

        var stub = UnifiedChatMessage.User(summaryResult.Summary);
        stub.Kind = MessageKind.CompactStub;
        stub.InputTokens = summaryResult.InputTokens;
        stub.OutputTokens = summaryResult.FinalSummaryTokens;
        stub.CachedTokens = summaryResult.CachedTokens;
        stub.MetaJson = JsonSerializer.Serialize(
            new CompactStubMeta
            {
                V = 2,
                RemovedCount = removed,
                FromSeq = fromSeq,
                ToSeq = toSeq,
                CoveredFromMessageId = coveredFrom,
                CoveredToMessageId = coveredTo,
                CoveredMessageCount = removed,
                SummarizedMessageCount = summaryResult.SummarizedMessageCount,
                ExcludedMessageCount = summaryResult.ExcludedMessageCount + concurrentExcluded,
                TruncatedMessageCount = summaryResult.TruncatedMessageCount,
                SummaryChunkCount = summaryResult.ChunkCount,
                SummaryStrategy = summaryResult.ChunkCount > 1 ? "map-reduce" : "single",
                CompactLlmCallCount = summaryResult.LlmCallCount,
                ProviderId = snapshot.Provider.Id,
                ModelId = snapshot.Model.Id,
                SummaryTokens = summaryResult.FinalSummaryTokens,
                PromptTokens = ClampTokenCount(summaryResult.InputTokens),
                CachedTokens = ClampTokenCount(summaryResult.CachedTokens),
                BackupFile = backupFile,
            },
            HaloJsonOptions.CompactCamelCase
        );

        var turn = new TurnSummary
        {
            TurnId = "cturn-" + stub.Id,
            Kind = TurnKind.Compact,
            IterEnd = 1,
            UserPreview = "上下文压缩",
            UserContent = summaryResult.Summary,
            RemovedMessages = removed,
            Blocks = 0,
            Tools = 0,
            Failed = 0,
            FinalBlockId = null,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        CompactCommitResult commit;
        try
        {
            commit = await service
                .AppendCompactTurnAsync(workspaceId, sessionId, stub, turn, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                HaloLogger.App.Error(
                    "[Compact] compact.stub.commit.failed ws="
                        + workspaceId
                        + " sess="
                        + sessionId
                        + " msg="
                        + stub.Id
                        + " turn="
                        + turn.TurnId
                        + " error="
                        + ex.GetType().Name,
                    ex
                );
            }
            catch { }
            return CompactionResult.Fail("COMPACT_STUB_WRITE_ERROR", "压缩桩写入失败，事务已回滚：" + ex.Message);
        }

        int kept = current?.Count + 1 ?? snapshot.SnapshotCount + 1;
        var after = await LoadCurrentMessagesOrNull(service, workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (after != null)
            kept = after.Count;
        string preview =
            summaryResult.Summary.Length > 200
                ? summaryResult.Summary.Substring(0, 200)
                : summaryResult.Summary;
        return CompactionResult.OkStub(
            removed,
            kept,
            stub.Id,
            turn.TurnId,
            summaryResult.FinalSummaryTokens,
            preview,
            backupFile,
            commit.MetadataWarning,
            summaryResult.LlmCallCount
        );
    }

    /// <summary>stub 快照：上一桩之后的全部活跃历史进入规划，keepLastGroups 不裁剪摘要输入。</summary>
    private async Task<CompactPreparation> BuildStubSnapshotAsync(
        WorkspaceSessionService service,
        SessionMetadata? sessionMeta,
        string workspaceId,
        string sessionId,
        string? providerId,
        string? modelId,
        CancellationToken ct
    )
    {
        var active = await service
            .GetActiveContextMessagesAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        string reason = Context.ActiveContext.PrepareCompactionReason(active);
        if (!string.IsNullOrEmpty(reason))
            return CompactPreparation.NoOp(reason);

        var full =
            await service.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false)
            ?? new List<UnifiedChatMessage>();
        var previousStub = await service
            .GetLastCompactStubAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        int lead = 0;
        while (
            lead < active.Count
            && string.Equals(active[lead].Role, "system", StringComparison.OrdinalIgnoreCase)
        )
            lead++;
        bool hasStub = Context.ActiveContext.FindLastStubIndex(active) >= 0;
        int bodyStart = Math.Min(active.Count, lead + (hasStub ? 1 : 0));
        var postStub = active.Skip(bodyStart).ToList();
        var (provider, model) = ResolveProviderAndModel(providerId, modelId, sessionMeta);
        var workspace = await service.GetWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
        string sessionHash = !string.IsNullOrWhiteSpace(sessionMeta?.SessionHash)
            ? sessionMeta!.SessionHash
            : HeaderVariableResolver.ComputeSessionHash(workspaceId, sessionId);
        var headerContext = new HeaderResolutionContext(
            workspace?.Id ?? workspaceId,
            workspace?.Name ?? string.Empty,
            workspace?.RootPath ?? string.Empty,
            sessionId,
            sessionHash,
            provider.Id,
            provider.Name,
            model.Id,
            Guid.NewGuid().ToString("N")
        );
        var plan = _summaryPlanner.Plan(postStub, previousStub?.Summary, model, ct);
        string? coveredFrom = postStub.FirstOrDefault()?.Id;
        string? coveredTo = postStub.LastOrDefault()?.Id;
        var (fromSeq, toSeq) = await service
            .GetMessageSequenceRangeAsync(
                workspaceId,
                sessionId,
                coveredFrom ?? string.Empty,
                coveredTo ?? string.Empty,
                ct
            )
            .ConfigureAwait(false);
        var snapshot = new CompactSnapshot
        {
            OldCount = postStub.Count,
            SnapshotCount = full.Count,
            FromSeq = fromSeq,
            ToSeq = toSeq,
            CoveredFromMessageId = coveredFrom,
            CoveredToMessageId = coveredTo,
            PreviousStubId = previousStub?.MessageId,
            FrozenCompactMode = CompactMode.Stub,
            PrevSummary = previousStub?.Summary,
            Provider = provider,
            Model = model,
            HeaderContext = headerContext,
            SummaryPlan = plan,
        };
        return CompactPreparation.Ready(snapshot);
    }

    private static async Task<List<UnifiedChatMessage>?> LoadCurrentMessagesOrNull(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        CancellationToken ct
    )
    {
        try
        {
            return await service
                .GetSessionMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static List<UnifiedChatMessage>? ResolveRetiredMessages(
        IReadOnlyList<UnifiedChatMessage>? current,
        CompactSnapshot snapshot
    )
    {
        if (current == null)
            return null;
        int lead = 0;
        while (
            lead < current.Count
            && string.Equals(current[lead].Role, "system", StringComparison.OrdinalIgnoreCase)
        )
            lead++;
        int start = lead;
        if (!string.IsNullOrWhiteSpace(snapshot.PreviousStubId))
        {
            int previousIndex = -1;
            for (int i = 0; i < current.Count; i++)
            {
                if (string.Equals(current[i].Id, snapshot.PreviousStubId, StringComparison.Ordinal))
                {
                    previousIndex = i;
                    break;
                }
            }
            if (previousIndex >= 0)
                start = Math.Max(start, previousIndex + 1);
        }
        return current.Skip(start).ToList();
    }

    private static int ClampTokenCount(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}
