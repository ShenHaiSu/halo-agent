using System.Text;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
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

    /// <summary>P007：stub 快照（活跃切片语义）。摘要覆盖区间 = 活跃切片全体；上桩走 prevSummary 通道，不进 body。</summary>
    private async Task<CompactSnapshot?> BuildStubSnapshotAsync(
        WorkspaceSessionService service,
        SessionMetadata? sessionMeta,
        string workspaceId,
        string sessionId,
        string? providerId,
        string? modelId,
        int keepLastGroups,
        CancellationToken ct
    )
    {
        var active = await service
            .GetActiveContextMessagesAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (Context.ActiveContext.PrepareCompactionReason(active).Length > 0)
            return null; // EMPTY / NO_NEW_CONTENT / TOO_SHORT → 外层转 NoOp
        var full =
            await service.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false)
            ?? new List<UnifiedChatMessage>();
        var prevStub = await service
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
        // P007：keepLastGroups 降级为摘要输入尾部组上限（不再截断上下文，只收敛摘要输入）。
        List<UnifiedChatMessage> oldRegion = postStub;
        if (keepLastGroups > 0 && postStub.Count > 0)
        {
            var groups = _budgetService.GroupIntoAtomicTurns(postStub);
            int take = Math.Min(keepLastGroups, groups.Count);
            oldRegion = new List<UnifiedChatMessage>();
            for (int gi = groups.Count - take; gi < groups.Count; gi++)
                oldRegion.AddRange(groups[gi].Messages);
        }
        // P007：prevSummary 独立通道（首条上轮摘要）+ 降级 + 分片上限接线。
        var inputMessages = CompactionSummaryInput.BuildInputMessages(
            prevStub?.Summary,
            oldRegion,
            null
        );
        var reduced = CompactionSummaryInput.ReduceSummaryInput(inputMessages);
        var body = new StringBuilder();
        foreach (var m in reduced)
        {
            ct.ThrowIfCancellationRequested();
            if (m == null)
                continue;
            string role = (m.Role ?? string.Empty).ToLowerInvariant();
            if (string.Equals(role, "tool", StringComparison.Ordinal))
                body.AppendLine("[工具结果折叠：1条过程块，计数保留，原文不展开]");
            else if (string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    foreach (var tc in m.ToolCalls)
                        body.Append(
                                Truncate(
                                    tc.FunctionName + ":" + (tc.ArgumentsJson ?? string.Empty),
                                    TruncPerMessage
                                )
                            )
                            .Append(' ');
                    body.AppendLine();
                }
                body.Append("[助手正文保留] ")
                    .AppendLine(Truncate(m.Content ?? string.Empty, TruncPerMessage));
            }
            else
            {
                body.Append('[').Append(RoleLabel(m.Role)).Append("] ");
                if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    foreach (var tc in m.ToolCalls)
                        body.Append(
                                Truncate(
                                    tc.FunctionName + ":" + (tc.ArgumentsJson ?? string.Empty),
                                    TruncPerMessage
                                )
                            )
                            .Append(' ');
                }
                body.AppendLine(Truncate(m.Content ?? string.Empty, TruncPerMessage));
            }
        }
        var (provider, model) = ResolveProviderAndModel(providerId, modelId, sessionMeta);
        int maxTokens = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 4096;
        int inputLimit = CompactionSummaryInput.ComputeInputLimit(
            ContextBudgetLimits.FromModel(model),
            maxTokens
        );
        string bodyText = body.ToString();
        if (CompactionSummaryInput.EstimateSummaryTokens(bodyText, null, null) > inputLimit)
        {
            var chunks = CompactionSummaryInput.PlanSummaryChunks(bodyText, inputLimit);
            if (chunks.Count > 0)
                bodyText = chunks[^1]; // 超限保尾
        }
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
        var request = new UnifiedLlmRequest
        {
            RequestId = headerContext.RequestId,
            Provider = provider,
            Model = model,
            HeaderContext = headerContext,
            SystemInstructions = CompressorSystemPrompt,
            Messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User(bodyText) },
            Tools = Array.Empty<UnifiedToolDefinition>(),
        };
        // P007 会话级冻结：本快照仅在会话级 mode==stub 时进入；首次写桩由 AppendCompactTurnAsync 固化 meta.CompactMode=stub。
        // P007 全局切回 legacy 后有桩会话仍走 stub；无桩会话读侧切片恒等（零影响）。
        return new CompactSnapshot
        {
            OrigSystem = lead > 0 ? active[0] : null,
            OldCount = postStub.Count, // 活跃切片中桩后条数（覆盖口径，含被 keepLastGroups 裁掉的输入）
            SnapshotCount = full.Count,
            FromSeq = prevStub?.Seq,
            ToSeq = null, // DB seq 在此层不可得，保持 null（可空契约）
            FrozenCompactMode = CompactMode.Stub,
            PrevSummary = prevStub?.Summary,
            Provider = provider,
            Model = model,
            Request = request,
        };
    }
}
