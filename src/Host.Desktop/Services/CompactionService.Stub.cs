using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

public sealed partial class CompactionService
{
    /// <summary>P007：把摘要落成桩（append-only），不做任何 DELETE / 重写。</summary>
    private async Task<CompactionResult> AppendStubAsync(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        CompactSnapshot snapshot,
        string summary,
        UnifiedTokenUsage? compactUsage,
        string? backupFile,
        CancellationToken ct
    )
    {
        var stub = UnifiedChatMessage.User(summary);
        stub.Kind = MessageKind.CompactStub;
        stub.InputTokens = compactUsage?.InputTokens ?? 0;
        stub.OutputTokens = compactUsage?.OutputTokens ?? 0;
        stub.CachedTokens = compactUsage?.CachedInputTokens ?? 0;
        stub.MetaJson = JsonSerializer.Serialize(
            new CompactStubMeta
            {
                RemovedCount = snapshot.OldCount,
                FromSeq = snapshot.FromSeq,
                ToSeq = snapshot.ToSeq,
                ProviderId = snapshot.Provider.Id,
                ModelId = snapshot.Model.Id,
                SummaryTokens = (int)(stub.OutputTokens ?? 0),
                PromptTokens = (int)(stub.InputTokens ?? 0),
                CachedTokens = (int)(stub.CachedTokens ?? 0),
                BackupFile = backupFile,
            },
            HaloJsonOptions.CompactCamelCase
        );

        // 1) 消息追加：jsonl append + DB INSERT（复用既有双写，但绕开标题/账单副作用）。
        // P007：桩是 user role，禁止触发首条 user 标题逻辑；账单（Cumulative*/Total*）由 AppendCompactTurnAsync 冻结不动。
        try
        {
            await service
                .SessionRepository.AppendMessageAsync(workspaceId, sessionId, stub, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                HaloLogger.App.Error($"[Compact] stub message append failed: {ex.Message}", ex);
            }
            catch { }
            return CompactionResult.Fail("COMPACT_STUB_WRITE_ERROR", "压缩桩消息写入失败：" + ex.Message);
        }

        // 2) 压缩轮追加（MAX(turn_seq)+1，kind='compact'，无块）。
        var turn = new TurnSummary
        {
            TurnId = "cturn-" + stub.Id,
            Kind = TurnKind.Compact,
            IterEnd = 1,
            UserPreview = "上下文压缩",
            UserContent = summary,
            RemovedMessages = snapshot.OldCount,
            Blocks = 0,
            Tools = 0,
            Failed = 0,
            FinalBlockId = null,
            Status = TimelineTurnStatus.Completed,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        try
        {
            await service
                .AppendCompactTurnAsync(workspaceId, sessionId, stub, turn, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // P007：append-only 下消息可能已落（可接受）；不做删除回滚，提示用户重试压缩。
            try
            {
                HaloLogger.App.Error($"[Compact] stub turn append failed: {ex.Message}", ex);
            }
            catch { }
            return CompactionResult.Fail(
                "COMPACT_STUB_WRITE_ERROR",
                "压缩桩轮写入失败（消息可能已追加），请重试压缩：" + ex.Message
            );
        }

        // 3) 全量条数（append-only：压缩前 +1；并发追加路径允许多于 +1）。
        int kept = snapshot.SnapshotCount + 1;
        try
        {
            var all = await service
                .GetSessionMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (all != null && all.Count > kept)
                kept = all.Count;
        }
        catch { }

        string preview = summary.Length > 200 ? summary.Substring(0, 200) : summary;
        return CompactionResult.OkStub(
            snapshot.OldCount,
            kept,
            stub.Id,
            turn.TurnId,
            (int)(stub.OutputTokens ?? 0),
            preview,
            backupFile
        );
    }
}
