using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>
/// 上下文压缩服务（模型摘要 + 原子重写 + 不可中断）。
/// 压缩语义对齐上游压缩上下文（系统提示 + 摘要 + 最近若干轮，失败不动，累计不清零），
/// 本项目以工作区库为请求源，重写后保留可审计备份。
/// 三段式（快照拷贝→释锁→模型摘要→重加锁重写），压缩耗时不再占用追加锁临界区；
/// 压缩中查询与追加可用；取消经调用方接入。
/// </summary>
public sealed partial class CompactionService : ICompactionService
{
    public const int KeepDefaultGroups = 6;
    public const int TruncPerMessage = 2000;

    /// <summary>压缩器 system 提示词（冻结文案）。</summary>
    public const string CompressorSystemPrompt =
        "你是会话上下文压缩器。把用户提供的对话压缩成精炼的中文要点列表，必须保留：\n"
        + "- 任务目标与当前进展到哪一步\n"
        + "- 已完成的关键操作与结果（重要的文件路径、命令、数据结论）\n"
        + "- 用户的明确决定、偏好与要求\n"
        + "- 尚未完成、需要继续处理的事项\n"
        + "- 最近一轮的具体结论与下一步动作（原文保留关键标识：文件路径、命令、参数名）\n"
        + "只输出要点，不要复述对话原文，不要添加对话里没有的内容。";

    private readonly IWorkspaceSessionService _sessionService;
    private readonly ITokenBudgetService _budgetService;
    private readonly ILlmGatewayClient _gatewayClient;
    private readonly ProviderConfigService? _providerConfigService;
    private readonly UsageMetricsService? _usageMetrics;

    public CompactionService(
        IWorkspaceSessionService sessionService,
        ITokenBudgetService budgetService,
        ILlmGatewayClient gatewayClient,
        ProviderConfigService? providerConfigService = null,
        UsageMetricsService? usageMetrics = null
    )
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _providerConfigService = providerConfigService;
        _usageMetrics = usageMetrics;
    }

    public async Task<CompactionResult> CompactAsync(
        string workspaceId,
        string sessionId,
        string? providerId = null,
        string? modelId = null,
        int keepLastGroups = 6,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return CompactionResult.Fail("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空");
        if (keepLastGroups < 1)
            keepLastGroups = KeepDefaultGroups;

        if (_sessionService is not WorkspaceSessionService concrete)
            return CompactionResult.Fail("UNSUPPORTED_SERVICE", "当前会话服务不支持压缩重写");

        // Phase 1（持锁，短临界区）：快照拷贝（消息复制 + 摘要 body 拼接 + 请求预组装），随后立即释锁。
        CompactSnapshot? snapshot;
        var appendLock = concrete.GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = await BuildSnapshotAsync(
                    concrete,
                    workspaceId,
                    sessionId,
                    providerId,
                    modelId,
                    keepLastGroups,
                    ct
                )
                .ConfigureAwait(false);
        }
        finally
        {
            appendLock.Release();
        }
        if (snapshot == null)
            return CompactionResult.NoOp();

        // Phase 2（无锁）：经网关发起一次非流式调用（复用 StreamAsync 累积文本，不新增 HTTP 通道；tools 置空）。
        // ct 直连调用方：调用方可取消压缩，取消后落取消码。
        var request = snapshot.Request;
        var summaryAcc = new StringBuilder();
        UnifiedTokenUsage? compactUsage = null;
        string compactStatus = "ok";
        string compactError = string.Empty;
        try
        {
            await foreach (var evt in _gatewayClient.StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (
                    evt.Kind == UnifiedStreamEventKind.TextDelta
                    && !string.IsNullOrEmpty(evt.TextDelta)
                )
                    summaryAcc.Append(evt.TextDelta);
                else if (evt.Kind == UnifiedStreamEventKind.UsageUpdated && evt.Usage != null)
                    compactUsage = evt.Usage;
                else if (evt.Kind == UnifiedStreamEventKind.StreamError)
                {
                    compactStatus = "error";
                    compactError = evt.ErrorMessage ?? "摘要模型调用失败";
                    break;
                }
                else if (evt.Kind == UnifiedStreamEventKind.StreamCancelled)
                {
                    compactStatus = "cancelled";
                    compactError = "压缩被取消";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            compactStatus = "cancelled";
            compactError = "压缩被取消";
        }
        catch (Exception ex)
        {
            compactStatus = "error";
            compactError = ex.Message;
        }
        // 压缩请求同样落袋（成功、取消与失败都记；有实测用实测，无实测用估算）。
        try
        {
            var compactRows = new List<PendingUsageRow>();
            UsageRecorder.CollectAuto(
                compactRows,
                snapshot.Provider,
                snapshot.Model,
                compactUsage,
                UsageRecorder.EstimateInput(request.Messages, request.SystemInstructions),
                UsageRecorder.EstimateOutput(summaryAcc.ToString()),
                compactStatus,
                request.RequestId,
                sessionId,
                workspaceId,
                UsageRecorder.SourceCompact
            );
            UsageRecorder.Flush(_usageMetrics, compactRows, sessionId, workspaceId);
        }
        catch
        { /* 用量落袋永不影响压缩主流程 */
        }
        if (compactStatus == "cancelled")
            return CompactionResult.Fail(
                "COMPACT_CANCELLED",
                string.IsNullOrEmpty(compactError) ? "压缩被取消" : compactError
            );
        if (compactStatus == "error")
            return CompactionResult.Fail(
                "COMPACT_LLM_ERROR",
                string.IsNullOrEmpty(compactError) ? "摘要模型调用失败" : compactError
            );
        string summary = summaryAcc.ToString().Trim();
        if (string.IsNullOrWhiteSpace(summary))
            return CompactionResult.Fail("COMPACT_EMPTY", "摘要模型返回为空");

        // Phase 3（重加锁，短临界区）：桩模式走追加写入，旧模式走物理重写。
        // 压缩模式唯一口径为快照中的冻结模式（会话冻结优先，全局次之，缺省为旧模式）。
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string mode = string.IsNullOrWhiteSpace(snapshot.FrozenCompactMode)
                ? CompactMode.Legacy
                : snapshot.FrozenCompactMode.Trim();
            if (string.Equals(mode, CompactMode.Stub, StringComparison.OrdinalIgnoreCase))
            {
                // 桩模式下先对工作区库做在线快照，失败只告警不阻断（备份路径为空时后续恢复不可用，需明示）。
                string? backupFile = null;
                try
                {
                    var workspaceStore = new WorkspaceSessionStore(
                        concrete.SessionRepository.Storage
                    );
                    var backupInfo = await workspaceStore
                        .BackupWorkspaceAsync(workspaceId, ct)
                        .ConfigureAwait(false);
                    backupFile = backupInfo?.BackupPath;
                }
                catch (Exception ex)
                {
                    try
                    {
                        Infrastructure.Logging.HaloLogger.App.Warn(
                            $"[Compact] stub backup failed, continue without backup: {ex.Message}"
                        );
                    }
                    catch { }
                    backupFile = null;
                }
                return await AppendStubAsync(
                        concrete,
                        workspaceId,
                        sessionId,
                        snapshot,
                        summary,
                        compactUsage,
                        backupFile,
                        ct
                    )
                    .ConfigureAwait(false);
            }
            return await RewriteAsync(concrete, workspaceId, sessionId, snapshot, summary, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            appendLock.Release();
        }
    }

    /// <summary>压缩快照（锁内拷贝，锁外消费）。</summary>
    private sealed class CompactSnapshot
    {
        // 以下三字段仅旧重写路径使用（桩路径只读 OldCount 与请求相关字段）。
        public UnifiedChatMessage? OrigSystem;
        public List<UnifiedChatMessage> AnchorMessages = new();
        public List<UnifiedChatMessage> TailMessages = new();
        public int OldCount;
        public int SnapshotCount;

        // 桩区间（库可得时填写，否则为 null）。
        public long? FromSeq;
        public long? ToSeq;

        // 快照时解析的压缩模式（会话级冻结优先，全局次之）。
        public string FrozenCompactMode = "legacy";

        // 上轮桩摘要（多轮压缩经独立通道装配，不进摘要输入）。
        public string? PrevSummary;
        public ProviderConfig Provider = null!;
        public ModelConfig Model = null!;
        public UnifiedLlmRequest Request = null!;
    }

    private async Task<CompactSnapshot?> BuildSnapshotAsync(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        string? providerId,
        string? modelId,
        int keepLastGroups,
        CancellationToken ct
    )
    {
        // 桩模式先行（活跃切片语义，见桩快照）；旧模式走全量快照。
        SessionMetadata? earlySession = null;
        try
        {
            earlySession = await service
                .GetSessionAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
        }
        catch { }
        if (
            string.Equals(
                ResolveCompactMode(earlySession, service),
                CompactMode.Stub,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return await BuildStubSnapshotAsync(
                    service,
                    earlySession,
                    workspaceId,
                    sessionId,
                    providerId,
                    modelId,
                    keepLastGroups,
                    ct
                )
                .ConfigureAwait(false);

        // 1. load messages；太短 → null（外层转 NoOp，不动盘、无 bak，前端提示“还短无需压缩”）
        var messages =
            await service.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false)
            ?? new List<UnifiedChatMessage>();
        if (messages.Count <= keepLastGroups + 2)
            return null;

        // 2. 原子组切分（排除首条 system 后的全部）；anchor = 首个含 user 组及之前；tail = 末 keepLastGroups 组
        UnifiedChatMessage? origSystem = null;
        IReadOnlyList<UnifiedChatMessage> rest = messages;
        if (
            messages.Count > 0
            && string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase)
        )
        {
            origSystem = messages[0];
            rest = messages.Skip(1).ToList();
        }
        var groups = _budgetService.GroupIntoAtomicTurns(rest);
        if (groups.Count == 0)
            return null;

        int firstUserGroupIndex = groups.FindIndex(g =>
            g.Messages.Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
        );
        if (firstUserGroupIndex < 0)
            firstUserGroupIndex = 0;
        int anchorCount = firstUserGroupIndex + 1;
        int tailStart = Math.Max(anchorCount, groups.Count - keepLastGroups);
        var oldGroups = groups
            .Skip(anchorCount)
            .Take(Math.Max(0, tailStart - anchorCount))
            .ToList();
        var tailGroups = groups.Skip(tailStart).ToList();
        int oldCount = oldGroups.Sum(g => g.Messages.Count);
        if (oldCount == 0)
            return null;

        // 3. 摘要输入按时间轴语义组装：工具结果折叠计数，助手正文保留。
        var body = new StringBuilder();
        foreach (var g in oldGroups)
        {
            foreach (var m in g.Messages)
            {
                ct.ThrowIfCancellationRequested();
                string role = (m.Role ?? string.Empty).ToLowerInvariant();
                if (string.Equals(role, "tool", StringComparison.Ordinal))
                {
                    body.AppendLine("[工具结果折叠：1条过程块，计数保留，原文不展开]");
                }
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
                    // 终态正文保留：正文全留（截断长度与旧盘一致）。
                    body.Append("[助手正文保留] ")
                        .AppendLine(Truncate(m.Content ?? string.Empty, TruncPerMessage));
                }
                else
                {
                    body.Append('[').Append(RoleLabel(m.Role)).Append("] ");
                    body.AppendLine(Truncate(m.Content ?? string.Empty, TruncPerMessage));
                }
            }
        }

        // 4. 预组装网关请求（provider/model/header 均在锁内快照，锁外直接使用）
        var workspace = await service.GetWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
        var session = await service
            .GetSessionAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        var (provider, model) = ResolveProviderAndModel(providerId, modelId, session);
        string sessionHash = !string.IsNullOrWhiteSpace(session?.SessionHash)
            ? session!.SessionHash
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
            Messages = new List<UnifiedChatMessage> { UnifiedChatMessage.User(body.ToString()) },
            Tools = Array.Empty<UnifiedToolDefinition>(),
        };

        var snapshot = new CompactSnapshot
        {
            OrigSystem = origSystem,
            OldCount = oldCount,
            SnapshotCount = messages.Count,
            // 快照时不解析区间序列号（读全量；区间上下界暂空，由库路径补齐）。
            FromSeq = null,
            ToSeq = null,
            FrozenCompactMode = ResolveCompactMode(session, service),
            Provider = provider,
            Model = model,
            Request = request,
        };
        for (int i = 0; i < anchorCount; i++)
            snapshot.AnchorMessages.AddRange(groups[i].Messages);
        foreach (var g in tailGroups)
            snapshot.TailMessages.AddRange(g.Messages);
        return snapshot;
    }

    /// <summary>旧模式全量重写：工作区快照兜底，分区重写消息并截断被摘要覆盖的旧轮次。</summary>
    private async Task<CompactionResult> RewriteAsync(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        CompactSnapshot snapshot,
        string summary,
        CancellationToken ct
    )
    {
        // 组装 newMessages = [origSystem] + anchorGroups + [summaryMsg] + tailGroups + 压缩期间新追加
        var summaryMsg = UnifiedChatMessage.User(
            "【上下文摘要：压缩自此前 " + snapshot.OldCount + " 条，由模型自动生成，细节以备份为准】\n" + summary
        );
        summaryMsg.Kind = MessageKind.Chat;
        var newMessages = new List<UnifiedChatMessage>();
        if (snapshot.OrigSystem != null)
            newMessages.Add(snapshot.OrigSystem);
        newMessages.AddRange(snapshot.AnchorMessages);
        newMessages.Add(summaryMsg);
        newMessages.AddRange(snapshot.TailMessages);
        try
        {
            var current = await service
                .GetSessionMessagesAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            if (current != null && current.Count > snapshot.SnapshotCount)
                newMessages.AddRange(current.Skip(snapshot.SnapshotCount));
        }
        catch
        { /* 重载失败则按快照重写，不阻塞压缩 */
        }

        // 工作区快照兜底（快照内已做临时文件加原子替换，直接使用返回的备份路径）。
        var storage = service.SessionRepository.Storage;
        var historyStore = new SessionHistoryStore(new SessionHistoryDb(storage));
        string workspaceBackupPath;
        try
        {
            var workspaceStore = new WorkspaceSessionStore(storage);
            var backupInfo = await workspaceStore
                .BackupWorkspaceAsync(workspaceId, ct)
                .ConfigureAwait(false);
            workspaceBackupPath = string.IsNullOrWhiteSpace(backupInfo?.BackupPath)
                ? storage.GetWorkspaceBackupPath(workspaceId)
                : backupInfo.BackupPath;
        }
        catch (Exception ex)
        {
            return CompactionResult.Fail("COMPACT_REWRITE_ERROR", "工作区快照失败，未改动数据：" + ex.Message);
        }

        try
        {
            // 消息分区按新序列全量重写。
            await historyStore
                .RewriteMessagesAsync(workspaceId, sessionId, newMessages, ct)
                .ConfigureAwait(false);

            // 被摘要覆盖的旧轮次：按保留集合算出最小受影响序号后截断。
            var summaries = await historyStore
                .GetTimelineSummaryAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            var keepUserIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in snapshot.AnchorMessages)
                if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                    keepUserIds.Add(m.Id);
            foreach (var m in snapshot.TailMessages)
                if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                    keepUserIds.Add(m.Id);
            foreach (var m in newMessages)
                if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                    keepUserIds.Add(m.Id);
            // 摘要消息本身为 user，加入保持集合。
            keepUserIds.Add(summaryMsg.Id);
            long minDoomed = long.MaxValue;
            foreach (var s in summaries)
            {
                string tid = s.TurnId ?? string.Empty;
                string suffix = tid.StartsWith("uturn-", StringComparison.Ordinal)
                    ? tid.Substring(6)
                    : tid;
                if (
                    !keepUserIds.Contains(suffix)
                    && !string.Equals(suffix, summaryMsg.Id, StringComparison.Ordinal)
                )
                {
                    if (s.TurnSeq < minDoomed)
                        minDoomed = s.TurnSeq;
                }
            }
            if (minDoomed != long.MaxValue)
                await historyStore
                    .TruncateFromTurnSeqAsync(workspaceId, sessionId, minDoomed, ct)
                    .ConfigureAwait(false);

            // 插入摘要轮（摘要正文作为终态文本块落盘）。
            try
            {
                var sumTurnId = "uturn-" + summaryMsg.Id;
                long seqBase = 0;
                try
                {
                    var cur = await historyStore
                        .GetTimelineSummaryAsync(workspaceId, sessionId, ct)
                        .ConfigureAwait(false);
                    foreach (var s in cur)
                        if (s.TurnSeq > seqBase)
                            seqBase = s.TurnSeq;
                }
                catch { }
                var sumTurn = new Host.Desktop.Core.Models.TurnSummary
                {
                    TurnId = sumTurnId,
                    TurnSeq = seqBase + 1,
                    IterEnd = 1,
                    UserPreview = summary.Length > 15 ? summary.Substring(0, 15) : summary,
                    UserContent = summary,
                    FinalBlockId = "compact:" + sumTurnId + ":final",
                    FinalPreview = summary.Length > 60 ? summary.Substring(0, 60) : summary,
                    Blocks = 1,
                    Tools = 0,
                    Status = Host.Desktop.Core.Models.TimelineTurnStatus.Completed,
                    UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
                };
                var sumBlock = new Host.Desktop.Core.Models.TimelineBlock
                {
                    BlockId = sumTurn.FinalBlockId!,
                    TurnId = sumTurnId,
                    Iteration = 1,
                    Sequence = 1,
                    Kind = Host.Desktop.Core.Models.TimelineBlockKind.Text,
                    Tag = Host.Desktop.Core.Models.TimelineBlockTag.Final,
                    CharCount = summary.Length,
                    Preview = summary.Length > 200 ? summary.Substring(0, 200) : summary,
                    Content = summary,
                };
                await historyStore
                    .AppendTurnAsync(
                        workspaceId,
                        sessionId,
                        new List<Host.Desktop.Core.Models.UnifiedChatMessage> { summaryMsg },
                        new List<Host.Desktop.Core.Models.TimelineBlock> { sumBlock },
                        sumTurn,
                        ct
                    )
                    .ConfigureAwait(false);
            }
            catch { }

            // 回收孤儿工具输出。
            try
            {
                await historyStore
                    .CollectOrphanToolOutputsAsync(workspaceId, sessionId, ct)
                    .ConfigureAwait(false);
            }
            catch { }

            // 成功后删除工作区快照。
            try
            {
                if (File.Exists(workspaceBackupPath))
                    File.Delete(workspaceBackupPath);
            }
            catch { }
        }
        catch (Exception ex)
        {
            // 按会话分区从快照库回滚当前会话，其他会话不受影响。
            try
            {
                RestoreSessionPartitionFromBackup(
                    storage,
                    workspaceId,
                    sessionId,
                    workspaceBackupPath
                );
            }
            catch { }
            return CompactionResult.Fail("COMPACT_REWRITE_ERROR", "重写失败已回滚（备份保留）：" + ex.Message);
        }

        var meta = await service.GetSessionAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        if (meta != null)
        {
            meta.MessageCount = newMessages.Count;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await service
                .RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct)
                .ConfigureAwait(false);
        }

        // 成功（累计用量保持旧值不动；下轮用量到账前上一轮展示为未知）。
        string preview = summary.Length > 200 ? summary.Substring(0, 200) : summary;
        int keptTail = snapshot.TailMessages.Count;
        return CompactionResult.Ok(snapshot.OldCount, keptTail, preview, workspaceBackupPath);
    }

    /// <summary>重写失败时按会话分区从快照库覆盖回滚（仅本会话，不整库替换）。</summary>
    private static void RestoreSessionPartitionFromBackup(
        StoragePathManager storage,
        string workspaceId,
        string sessionId,
        string backupPath
    )
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            return;
        string dbPath = storage.GetWorkspaceDbPath(workspaceId);
        if (!File.Exists(dbPath))
            return;
        try
        {
            new SessionHistoryDb(storage).CheckpointWorkspace(workspaceId);
        }
        catch { }
        string escaped = backupPath.Replace("'", "''");
        using var conn = SessionHistoryDb.OpenConnection(dbPath);
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH '" + escaped + "' AS compact_bak;";
            attach.ExecuteNonQuery();
        }
        try
        {
            using var tx = conn.BeginTransaction();
            using (var delMessages = conn.CreateCommand())
            {
                delMessages.Transaction = tx;
                delMessages.CommandText = "DELETE FROM messages WHERE session_id = $sid;";
                delMessages.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                delMessages.ExecuteNonQuery();
            }
            using (var copyMessages = conn.CreateCommand())
            {
                copyMessages.Transaction = tx;
                copyMessages.CommandText =
                    "INSERT INTO messages SELECT * FROM compact_bak.messages WHERE session_id = $sid;";
                copyMessages.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                copyMessages.ExecuteNonQuery();
            }
            using (var delBlocks = conn.CreateCommand())
            {
                delBlocks.Transaction = tx;
                delBlocks.CommandText = "DELETE FROM blocks WHERE session_id = $sid;";
                delBlocks.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                delBlocks.ExecuteNonQuery();
            }
            using (var copyBlocks = conn.CreateCommand())
            {
                copyBlocks.Transaction = tx;
                copyBlocks.CommandText =
                    "INSERT INTO blocks SELECT * FROM compact_bak.blocks WHERE session_id = $sid;";
                copyBlocks.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                copyBlocks.ExecuteNonQuery();
            }
            using (var delTurns = conn.CreateCommand())
            {
                delTurns.Transaction = tx;
                delTurns.CommandText = "DELETE FROM turns WHERE session_id = $sid;";
                delTurns.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                delTurns.ExecuteNonQuery();
            }
            using (var copyTurns = conn.CreateCommand())
            {
                copyTurns.Transaction = tx;
                copyTurns.CommandText =
                    "INSERT INTO turns SELECT * FROM compact_bak.turns WHERE session_id = $sid;";
                copyTurns.Parameters.AddWithValue("$sid", sessionId ?? string.Empty);
                copyTurns.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally
        {
            using var detach = conn.CreateCommand();
            detach.CommandText = "DETACH compact_bak;";
            detach.ExecuteNonQuery();
        }
    }

    private (ProviderConfig Provider, ModelConfig Model) ResolveProviderAndModel(
        string? providerId,
        string? modelId,
        SessionMetadata? session
    )
    {
        ProviderConfig? provider = null;
        if (_providerConfigService != null)
        {
            try
            {
                string pid = !string.IsNullOrWhiteSpace(providerId)
                    ? providerId!
                    : (session?.ProviderId ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(pid))
                    provider = _providerConfigService.GetProviderById(pid);
                if (provider == null)
                {
                    var doc = _providerConfigService.GetProvidersConfigInternal();
                    provider =
                        doc.Providers.FirstOrDefault(p => p.Enabled)
                        ?? doc.Providers.FirstOrDefault();
                }
            }
            catch
            {
                provider = null;
            }
        }
        provider ??= new ProviderConfig
        {
            Id = string.IsNullOrWhiteSpace(providerId) ? "provider_default" : providerId!,
            Name = "DefaultProvider",
            BaseUrl = "https://api.openai.com/v1"
        };
        string mid = !string.IsNullOrWhiteSpace(modelId)
            ? modelId!
            : (session?.ModelId ?? string.Empty);
        ModelConfig? model = null;
        if (!string.IsNullOrWhiteSpace(mid))
            model = provider.Models.FirstOrDefault(m =>
                string.Equals(m.Id, mid, StringComparison.OrdinalIgnoreCase)
            );
        model ??=
            provider.Models.FirstOrDefault(m => m.Enabled)
            ?? provider.Models.FirstOrDefault()
            ?? new ModelConfig
            {
                Id = "gpt-4o",
                Protocol = LlmProtocolType.Chat,
                MaxContextTokens = 128000,
                MaxOutputTokens = 4096
            };
        return (provider, model);
    }

    /// <summary>压缩模式唯一口径（会话级冻结优先，全局次之；默认旧模式）。</summary>
    private static string ResolveCompactMode(
        SessionMetadata? session,
        WorkspaceSessionService service
    )
    {
        if (!string.IsNullOrWhiteSpace(session?.CompactMode))
            return session!.CompactMode!.Trim();
        try
        {
            var global = service.SessionRepository.Storage.LoadAppSettings();
            if (global != null && !string.IsNullOrWhiteSpace(global.CompactMode))
                return global.CompactMode.Trim();
        }
        catch { }
        return CompactMode.Legacy;
    }

    private static string RoleLabel(string? role)
    {
        if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            return "用户";
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            return "助手";
        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            return "工具结果";
        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            return "系统";
        return role ?? "未知";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s ?? string.Empty;
        return s.Substring(0, max);
    }
}
