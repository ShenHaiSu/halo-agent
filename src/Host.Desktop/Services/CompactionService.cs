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
    private readonly CompactionSummaryPlanner _summaryPlanner;
    private readonly CompactionSummaryExecutor _summaryExecutor;

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
        _summaryPlanner = new CompactionSummaryPlanner(_budgetService);
        _summaryExecutor = new CompactionSummaryExecutor(
            _gatewayClient,
            _budgetService,
            _usageMetrics
        );
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

        CompactPreparation preparation;
        var appendLock = concrete.GetSessionAppendLock(sessionId);
        try
        {
            await appendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CompactionResult.Fail("COMPACT_CANCELLED", "压缩被取消");
        }
        try
        {
            preparation = await BuildSnapshotAsync(
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
        catch (OperationCanceledException)
        {
            return CompactionResult.Fail("COMPACT_CANCELLED", "压缩被取消");
        }
        catch (CompactionSummaryException ex)
        {
            return CompactionResult.Fail(ex.Code, ex.Message);
        }
        finally
        {
            appendLock.Release();
        }
        if (!string.IsNullOrWhiteSpace(preparation.NoOpReason))
            return CompactionResult.NoOp(preparation.NoOpReason);
        var snapshot = preparation.Snapshot!;

        CompactionSummaryResult summaryResult;
        try
        {
            summaryResult = await _summaryExecutor
                .ExecuteAsync(
                    snapshot.SummaryPlan,
                    new CompactionSummaryExecutionContext(
                        snapshot.Provider,
                        snapshot.Model,
                        snapshot.HeaderContext,
                        workspaceId,
                        sessionId
                    ),
                    snapshot.PrevSummary,
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (CompactionSummaryException ex)
        {
            return CompactionResult.Fail(ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return CompactionResult.Fail("COMPACT_SUMMARY_CANCELLED", "压缩被取消");
        }
        catch (Exception ex)
        {
            return CompactionResult.Fail("COMPACT_LLM_ERROR", "摘要模型调用失败：" + ex.Message);
        }

        try
        {
            await appendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CompactionResult.Fail("COMPACT_CANCELLED", "压缩被取消");
        }
        try
        {
            string mode = string.IsNullOrWhiteSpace(snapshot.FrozenCompactMode)
                ? CompactMode.Legacy
                : snapshot.FrozenCompactMode.Trim();
            if (string.Equals(mode, CompactMode.Stub, StringComparison.OrdinalIgnoreCase))
            {
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
                            "[Compact] stub backup failed, continue without backup: "
                                + ex.GetType().Name
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
                        summaryResult,
                        backupFile,
                        ct
                    )
                    .ConfigureAwait(false);
            }
            return await RewriteAsync(concrete, workspaceId, sessionId, snapshot, summaryResult, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            appendLock.Release();
        }
    }

    private sealed class CompactPreparation
    {
        public CompactSnapshot? Snapshot { get; init; }
        public string? NoOpReason { get; init; }

        public static CompactPreparation NoOp(string reason) => new() { NoOpReason = reason };

        public static CompactPreparation Ready(CompactSnapshot snapshot) =>
            new() { Snapshot = snapshot };
    }

    /// <summary>压缩快照（锁内拷贝，锁外消费）。</summary>
    private sealed class CompactSnapshot
    {
        public UnifiedChatMessage? OrigSystem;
        public List<UnifiedChatMessage> AnchorMessages = new();
        public List<UnifiedChatMessage> TailMessages = new();
        public int OldCount;
        public int SnapshotCount;
        public long? FromSeq;
        public long? ToSeq;
        public string? CoveredFromMessageId;
        public string? CoveredToMessageId;
        public string? PreviousStubId;
        public string FrozenCompactMode = CompactMode.Legacy;
        public string? PrevSummary;
        public ProviderConfig Provider = null!;
        public ModelConfig Model = null!;
        public HeaderResolutionContext HeaderContext = null!;
        public CompactionSummaryPlan SummaryPlan = null!;
    }

    private async Task<CompactPreparation> BuildSnapshotAsync(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        string? providerId,
        string? modelId,
        int keepLastGroups,
        CancellationToken ct
    )
    {
        SessionMetadata? session = null;
        try
        {
            session = await service
                .GetSessionAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
        }
        catch { }
        string mode = await ResolveCompactModeAsync(session, service, workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (string.Equals(mode, CompactMode.Stub, StringComparison.OrdinalIgnoreCase))
            return await BuildStubSnapshotAsync(
                    service,
                    session,
                    workspaceId,
                    sessionId,
                    providerId,
                    modelId,
                    ct
                )
                .ConfigureAwait(false);

        var messages =
            await service.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false)
            ?? new List<UnifiedChatMessage>();
        if (messages.Count == 0)
            return CompactPreparation.NoOp(CompactionNoOpReasons.Empty);
        if (messages.Count <= keepLastGroups + 2)
            return CompactPreparation.NoOp(CompactionNoOpReasons.TooShort);

        UnifiedChatMessage? origSystem = null;
        IReadOnlyList<UnifiedChatMessage> rest = messages;
        if (string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            origSystem = messages[0];
            rest = messages.Skip(1).ToList();
        }
        var groups = _budgetService.GroupIntoAtomicTurns(rest);
        if (groups.Count == 0)
            return CompactPreparation.NoOp(CompactionNoOpReasons.TooShort);

        int firstUserGroupIndex = groups.FindIndex(group =>
            group.Messages.Any(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
            )
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
        var oldRegion = oldGroups.SelectMany(group => group.Messages).ToList();
        int oldCount = oldRegion.Count;
        if (oldCount == 0)
            return CompactPreparation.NoOp(CompactionNoOpReasons.TooShort);

        var workspace = await service.GetWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
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
        var plan = _summaryPlanner.Plan(oldRegion, null, model, ct);
        var snapshot = new CompactSnapshot
        {
            OrigSystem = origSystem,
            OldCount = oldCount,
            SnapshotCount = messages.Count,
            CoveredFromMessageId = oldRegion.FirstOrDefault()?.Id,
            CoveredToMessageId = oldRegion.LastOrDefault()?.Id,
            FrozenCompactMode = mode,
            Provider = provider,
            Model = model,
            HeaderContext = headerContext,
            SummaryPlan = plan,
        };
        for (int i = 0; i < anchorCount; i++)
            snapshot.AnchorMessages.AddRange(groups[i].Messages);
        foreach (var group in tailGroups)
            snapshot.TailMessages.AddRange(group.Messages);
        return CompactPreparation.Ready(snapshot);
    }

    /// <summary>旧模式全量重写：工作区快照兜底，分区重写消息并截断被摘要覆盖的旧轮次。</summary>
    private async Task<CompactionResult> RewriteAsync(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        CompactSnapshot snapshot,
        CompactionSummaryResult summaryResult,
        CancellationToken ct
    )
    {
        string summary = summaryResult.Summary;
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

            // 摘要消息已随分区重写落盘；这里只补零 block 的 compact 展示轮。
            var sumTurnId = "uturn-" + summaryMsg.Id;
            long seqBase = 0;
            var currentTurns = await historyStore
                .GetTimelineSummaryAsync(workspaceId, sessionId, ct)
                .ConfigureAwait(false);
            foreach (var turn in currentTurns)
                if (turn.TurnSeq > seqBase)
                    seqBase = turn.TurnSeq;
            var sumTurn = new TurnSummary
            {
                TurnId = sumTurnId,
                TurnSeq = seqBase + 1,
                Kind = TurnKind.Compact,
                IterEnd = 1,
                UserPreview = summary.Length > 15 ? summary.Substring(0, 15) : summary,
                UserContent = summary,
                RemovedMessages = snapshot.OldCount,
                FinalBlockId = null,
                FinalPreview = string.Empty,
                Blocks = 0,
                Tools = 0,
                Status = TimelineTurnStatus.Completed,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            };
            await historyStore
                .AppendTurnAsync(
                    workspaceId,
                    sessionId,
                    new List<UnifiedChatMessage> { summaryMsg },
                    Array.Empty<TimelineBlock>(),
                    sumTurn,
                    ct
                )
                .ConfigureAwait(false);

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
        return CompactionResult.Ok(
            snapshot.OldCount,
            newMessages.Count,
            preview,
            workspaceBackupPath,
            summaryResult.FinalSummaryTokens,
            summaryResult.LlmCallCount
        );
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

    /// <summary>已有桩的会话强制 stub；否则会话显式模式优先于全局模式。</summary>
    private static async Task<string> ResolveCompactModeAsync(
        SessionMetadata? session,
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        CancellationToken ct
    )
    {
        string? explicitMode = string.IsNullOrWhiteSpace(session?.CompactMode)
            ? null
            : session!.CompactMode!.Trim();
        var stub = await service
            .GetLastCompactStubAsync(workspaceId, sessionId, ct)
            .ConfigureAwait(false);
        if (stub != null)
            return CompactMode.Stub;
        if (!string.IsNullOrWhiteSpace(explicitMode))
            return explicitMode;
        try
        {
            var global = service.SessionRepository.Storage.LoadAppSettings();
            if (global != null && !string.IsNullOrWhiteSpace(global.CompactMode))
                return global.CompactMode.Trim();
        }
        catch { }
        return CompactMode.Legacy;
    }
}
