using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>need05-03：上下文压缩结果。</summary>
public sealed class CompactionResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int RemovedMessages { get; init; }
    public int KeptMessages { get; init; }
    /// <summary>摘要首 200 字，事件展示用。</summary>
    public string SummaryPreview { get; init; } = string.Empty;
    public string? BackupFileName { get; init; }

    public static CompactionResult Ok(int removed, int kept, string preview, string? backup) =>
        new() { Success = true, RemovedMessages = removed, KeptMessages = kept, SummaryPreview = preview ?? string.Empty, BackupFileName = backup };

    /// <summary>无事可做（太短无需压缩）：Success 且 Removed==0，前端据此提示。</summary>
    public static CompactionResult NoOp() => new() { Success = true };

    public static CompactionResult Fail(string code, string message) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

public interface ICompactionService
{
    Task<CompactionResult> CompactAsync(
        string workspaceId,
        string sessionId,
        string? providerId = null,
        string? modelId = null,
        int keepLastGroups = 6,
        CancellationToken ct = default);
}

/// <summary>
/// need05-03：/compact 上下文压缩服务（模型摘要 + 原子重写 + 不可中断）。
/// 学 znaide compact_context（system+摘要+最近 N 条，失败不动，累计不清零），
/// deliberate 差异：本项目 messages.jsonl 即请求源，必须重写（bak 保留可审计）。
/// </summary>
public sealed class CompactionService : ICompactionService
{
    public const int KeepDefaultGroups = 6;
    public const int TruncPerMessage = 2000;

    /// <summary>压缩器 system 提示词（冻结文案）。</summary>
    public const string CompressorSystemPrompt =
        "你是会话上下文压缩器。把用户提供的对话压缩成精炼的中文要点列表，必须保留：\n" +
        "- 任务目标与当前进展到哪一步\n" +
        "- 已完成的关键操作与结果（重要的文件路径、命令、数据结论）\n" +
        "- 用户的明确决定、偏好与要求\n" +
        "- 尚未完成、需要继续处理的事项\n" +
        "只输出要点，不要复述对话原文，不要添加对话里没有的内容。";

    private readonly IWorkspaceSessionService _sessionService;
    private readonly ITokenBudgetService _budgetService;
    private readonly ILlmGatewayClient _gatewayClient;
    private readonly ProviderConfigService? _providerConfigService;

    public CompactionService(
        IWorkspaceSessionService sessionService,
        ITokenBudgetService budgetService,
        ILlmGatewayClient gatewayClient,
        ProviderConfigService? providerConfigService = null)
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _providerConfigService = providerConfigService;
    }

    public async Task<CompactionResult> CompactAsync(
        string workspaceId,
        string sessionId,
        string? providerId = null,
        string? modelId = null,
        int keepLastGroups = 6,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            return CompactionResult.Fail("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空");
        if (keepLastGroups < 1) keepLastGroups = KeepDefaultGroups;

        if (_sessionService is not WorkspaceSessionService concrete)
            return CompactionResult.Fail("UNSUPPORTED_SERVICE", "当前会话服务不支持压缩重写");

        // 全程持追加锁：与 AppendSessionMessageAsync 同一把锁，天然互斥（不可中断契约）。
        var appendLock = concrete.GetSessionAppendLock(sessionId);
        await appendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CompactCoreAsync(concrete, workspaceId, sessionId, providerId, modelId, keepLastGroups, ct).ConfigureAwait(false);
        }
        finally
        {
            appendLock.Release();
        }
    }

    private async Task<CompactionResult> CompactCoreAsync(
        WorkspaceSessionService service,
        string workspaceId,
        string sessionId,
        string? providerId,
        string? modelId,
        int keepLastGroups,
        CancellationToken ct)
    {
        // 1. load messages；太短 → NoOp（不动盘、无 bak，前端提示“还短无需压缩”）
        var messages = await service.GetSessionMessagesAsync(workspaceId, sessionId, ct).ConfigureAwait(false)
            ?? new List<UnifiedChatMessage>();
        if (messages.Count <= keepLastGroups + 2)
            return CompactionResult.NoOp();

        // 2. 原子组切分（排除首条 system 后的全部）；anchor = 首个含 user 组及之前；tail = 末 keepLastGroups 组
        UnifiedChatMessage? origSystem = null;
        IReadOnlyList<UnifiedChatMessage> rest = messages;
        if (messages.Count > 0 && string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            origSystem = messages[0];
            rest = messages.Skip(1).ToList();
        }
        var groups = _budgetService.GroupIntoAtomicTurns(rest);
        if (groups.Count == 0)
            return CompactionResult.NoOp();

        int firstUserGroupIndex = groups.FindIndex(g => g.Messages.Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)));
        if (firstUserGroupIndex < 0) firstUserGroupIndex = 0;
        int anchorCount = firstUserGroupIndex + 1;
        int tailStart = Math.Max(anchorCount, groups.Count - keepLastGroups);
        var oldGroups = groups.Skip(anchorCount).Take(Math.Max(0, tailStart - anchorCount)).ToList();
        var tailGroups = groups.Skip(tailStart).ToList();
        int oldCount = oldGroups.Sum(g => g.Messages.Count);
        if (oldCount == 0)
            return CompactionResult.NoOp();

        // 3. body 拼接（tool 参数 ArgumentsJson 同截 2000）
        var body = new StringBuilder();
        foreach (var g in oldGroups)
        {
            foreach (var m in g.Messages)
            {
                ct.ThrowIfCancellationRequested();
                body.Append('[').Append(RoleLabel(m.Role)).Append("] ");
                if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    foreach (var tc in m.ToolCalls)
                        body.Append(Truncate(tc.FunctionName + ":" + (tc.ArgumentsJson ?? string.Empty), TruncPerMessage)).Append(' ');
                }
                body.AppendLine(Truncate(m.Content ?? string.Empty, TruncPerMessage));
            }
        }

        // 4. 经网关发起一次非流式调用（复用 StreamAsync 累积文本，不新增 HTTP 通道；tools 置空）
        var workspace = await service.GetWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
        var session = await service.GetSessionAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        var (provider, model) = ResolveProviderAndModel(providerId, modelId, session);
        string sessionHash = !string.IsNullOrWhiteSpace(session?.SessionHash)
            ? session!.SessionHash
            : HeaderVariableResolver.ComputeSessionHash(workspaceId, sessionId);
        var headerContext = new HeaderResolutionContext(
            workspace?.Id ?? workspaceId,
            workspace?.Name ?? string.Empty,
            workspace?.RootPath ?? string.Empty,
            sessionId, sessionHash,
            provider.Id, provider.Name, model.Id,
            Guid.NewGuid().ToString("N"));
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

        var summaryAcc = new StringBuilder();
        try
        {
            await foreach (var evt in _gatewayClient.StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (evt.Kind == UnifiedStreamEventKind.TextDelta && !string.IsNullOrEmpty(evt.TextDelta))
                    summaryAcc.Append(evt.TextDelta);
                else if (evt.Kind == UnifiedStreamEventKind.StreamError)
                    return CompactionResult.Fail("COMPACT_LLM_ERROR", evt.ErrorMessage ?? "摘要模型调用失败");
                else if (evt.Kind == UnifiedStreamEventKind.StreamCancelled)
                    return CompactionResult.Fail("COMPACT_CANCELLED", "压缩被取消");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CompactionResult.Fail("COMPACT_CANCELLED", "压缩被取消");
        }
        catch (Exception ex)
        {
            return CompactionResult.Fail("COMPACT_LLM_ERROR", ex.Message);
        }

        // 5. 空回复 → 失败（不动盘）
        string summary = summaryAcc.ToString().Trim();
        if (string.IsNullOrWhiteSpace(summary))
            return CompactionResult.Fail("COMPACT_EMPTY", "摘要模型返回为空");

        // 6-7. 组装 newMessages = [origSystem] + anchorGroups + [summaryMsg] + tailGroups
        var summaryMsg = UnifiedChatMessage.User(
            "【上下文摘要：压缩自此前 " + oldCount + " 条，由模型自动生成，细节以备份为准】\n" + summary);
        var newMessages = new List<UnifiedChatMessage>(messages.Count - oldCount + 1);
        if (origSystem != null) newMessages.Add(origSystem);
        for (int i = 0; i < anchorCount; i++) newMessages.AddRange(groups[i].Messages);
        newMessages.Add(summaryMsg);
        foreach (var g in tailGroups) newMessages.AddRange(g.Messages);

        // 8. 原子落盘：备份 → 重写 → meta（MessageCount/UpdatedAt；Cumulative*/Total* 不动，账单不清零）
        var repository = service.SessionRepository;
        string backupFileName = await repository.BackupMessagesJsonlAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        try
        {
            await repository.RewriteMessagesJsonlAsync(workspaceId, sessionId, newMessages, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CompactionResult.Fail("COMPACT_REWRITE_ERROR", "重写失败（备份已保留）：" + ex.Message);
        }
        var meta = await service.GetSessionAsync(workspaceId, sessionId, ct).ConfigureAwait(false);
        if (meta != null)
        {
            meta.MessageCount = newMessages.Count;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await service.RefreshMetaIndexAsync(workspaceId, sessionId, meta, ct).ConfigureAwait(false);
        }

        // 9. 成功（占用快照 TotalInput/TotalCached 保持旧值不动，LastPrompt 下轮 usage 到账前为 unknown，前端画 ?）
        string preview = summary.Length > 200 ? summary.Substring(0, 200) : summary;
        int keptTail = tailGroups.Sum(g => g.Messages.Count);
        return CompactionResult.Ok(oldCount, keptTail, preview, backupFileName);
    }

    private (ProviderConfig Provider, ModelConfig Model) ResolveProviderAndModel(string? providerId, string? modelId, SessionMetadata? session)
    {
        ProviderConfig? provider = null;
        if (_providerConfigService != null)
        {
            try
            {
                string pid = !string.IsNullOrWhiteSpace(providerId) ? providerId! : (session?.ProviderId ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(pid)) provider = _providerConfigService.GetProviderById(pid);
                if (provider == null)
                {
                    var doc = _providerConfigService.GetProvidersConfigInternal();
                    provider = doc.Providers.FirstOrDefault(p => p.Enabled) ?? doc.Providers.FirstOrDefault();
                }
            }
            catch { provider = null; }
        }
        provider ??= new ProviderConfig
        {
            Id = string.IsNullOrWhiteSpace(providerId) ? "provider_default" : providerId!,
            Name = "DefaultProvider",
            BaseUrl = "https://api.openai.com/v1"
        };
        string mid = !string.IsNullOrWhiteSpace(modelId) ? modelId! : (session?.ModelId ?? string.Empty);
        ModelConfig? model = null;
        if (!string.IsNullOrWhiteSpace(mid))
            model = provider.Models.FirstOrDefault(m => string.Equals(m.Id, mid, StringComparison.OrdinalIgnoreCase));
        model ??= provider.Models.FirstOrDefault(m => m.Enabled)
            ?? provider.Models.FirstOrDefault()
            ?? new ModelConfig { Id = "gpt-4o", Protocol = LlmProtocolType.Chat, MaxContextTokens = 128000, MaxOutputTokens = 4096 };
        return (provider, model);
    }

    private static string RoleLabel(string? role)
    {
        if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return "用户";
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) return "助手";
        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)) return "工具结果";
        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) return "系统";
        return role ?? "未知";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
        return s.Substring(0, max);
    }
}
