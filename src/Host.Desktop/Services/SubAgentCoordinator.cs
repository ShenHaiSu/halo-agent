using System.Collections.Concurrent;
using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

/// <summary>
/// need13-04：SubAgentCoordinator（只管建会话 + 起 Task + 等 + 取消 + 事件，不管 LLM 文本）。
/// </summary>
public sealed partial class SubAgentCoordinator
{
    private readonly ConcurrentDictionary<string, List<SubHandle>> _trees =
        new(StringComparer.Ordinal);
    private readonly IWorkspaceSessionService _sessions;
    private readonly ITokenBudgetService _budget;
    private readonly ILlmGatewayClient _gateway;
    private readonly StoragePathManager? _storageManager;
    private readonly ProviderConfigService? _providers;
    private readonly PromptConfigService? _prompts;
    private readonly AgentConfigService? _agentConfig;
    private readonly UsageMetricsService? _usage;

    public SubAgentCoordinator(
        IWorkspaceSessionService sessions,
        ITokenBudgetService budget,
        ILlmGatewayClient gateway,
        StoragePathManager? storageManager = null,
        ProviderConfigService? providers = null,
        PromptConfigService? prompts = null,
        AgentConfigService? agentConfig = null,
        UsageMetricsService? usage = null
    )
    {
        _sessions = sessions;
        _budget = budget;
        _gateway = gateway;
        _storageManager = storageManager;
        _providers = providers;
        _prompts = prompts;
        _agentConfig = agentConfig;
        _usage = usage;
    }

    /// <summary>need13：解析 spawn 参数（空→VALIDATION 拒；超 8000 截断+warning；model 非法回退+warning）。</summary>
    public static (SpawnCallSpec? Spec, string? ValidationFail, string? Warning) ParseSpawnCall(
        string callId,
        string argumentsJson
    )
    {
        string task = string.Empty,
            label = string.Empty,
            model = string.Empty,
            providerId = string.Empty;
        int? maxIter = null;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson
            );
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。", null);
            if (root.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.String)
                task = t.GetString() ?? string.Empty;
            if (root.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String)
                label = l.GetString() ?? string.Empty;
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                model = m.GetString() ?? string.Empty;
            if (root.TryGetProperty("providerId", out var p) && p.ValueKind == JsonValueKind.String)
                providerId = p.GetString() ?? string.Empty;
            if (
                root.TryGetProperty("maxIterations", out var mi)
                && mi.ValueKind == JsonValueKind.Number
                && mi.TryGetInt32(out int v)
            )
                maxIter = v;
        }
        catch
        {
            return (null, "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。", null);
        }
        if (string.IsNullOrWhiteSpace(task))
            return (null, "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。", null);
        string warning = string.Empty;
        string t2 = task.Trim();
        if (t2.Length > 8000)
        {
            t2 = t2[..8000];
            warning = "[SUBAGENT_VALIDATION] spawn 参数 task 超 8000 字已截断。";
        }
        string lb = label.Trim();
        if (lb.Length > 20)
            lb = lb[..20];
        if (string.IsNullOrEmpty(lb))
            lb = t2.Length > 12 ? t2[..12] : t2;
        int? miClamped = maxIter.HasValue ? Math.Clamp(maxIter.Value, 1, 50) : null;
        return (
            new SpawnCallSpec
            {
                CallId = callId,
                Task = t2,
                Label = lb,
                Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                ProviderId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
                MaxIterations = miClamped,
            },
            null,
            string.IsNullOrEmpty(warning) ? null : warning
        );
    }

    /// <summary>
    /// 建子：take 前 N 个（N=policy.SubMaxParallel），余下记 quotaRejected（不建会话，直接 Fail 回包）。
    /// </summary>
    public async Task<(
        List<SubHandle> Handles,
        List<(string CallId, string FailText)> Rejected
    )> CreateSubsAsync(
        string workspaceId,
        string mainSessionId,
        IReadOnlyList<SpawnCallSpec> spawnCalls,
        string mainProviderId,
        string mainModelId,
        SubPolicySnapshot policy,
        CancellationToken mainCt
    )
    {
        var handles = new List<SubHandle>();
        var rejected = new List<(string, string)>();
        int take = Math.Max(1, Math.Min(policy.SubMaxParallel, spawnCalls.Count));
        try
        {
            for (int i = 0; i < spawnCalls.Count; i++)
            {
                var spec = spawnCalls[i];
                if (i >= take)
                {
                    string quotaMsg =
                        $"[SUBAGENT_QUOTA_EXCEEDED] 本轮子代理并发超限（max={policy.SubMaxParallel}，本轮第{i + 1}个）。本次 spawn 已拒绝。请不要重试，先处理已启动的子结果，不足部分拆到下一轮或串行直做。";
                    rejected.Add((spec.CallId, quotaMsg));
                    // need23：落拒绝记录（设置中心·子运行监控卡可见）
                    RecordRejection("SUBAGENT_QUOTA_EXCEEDED", spec.CallId, quotaMsg);
                    continue;
                }
                // model 覆盖：存在且 Enabled 否则回退主模型 + warning
                string providerId = mainProviderId,
                    modelId = mainModelId;
                string? modelWarning = null;
                if (!string.IsNullOrWhiteSpace(spec.Model) && _providers != null)
                {
                    try
                    {
                        var doc = _providers.GetProvidersConfigInternal();
                        var found = doc
                            .Providers.SelectMany(p => p.Models.Select(m => (P: p, M: m)))
                            .FirstOrDefault(x =>
                                string.Equals(
                                    x.M.Id,
                                    spec.Model,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );
                        if (found.M != null && found.P.Enabled && found.M.Enabled)
                        {
                            providerId = found.P.Id;
                            modelId = found.M.Id;
                        }
                        else
                        {
                            modelWarning =
                                $"[SUBAGENT_VALIDATION] spawn 参数模型 {spec.Model} 不存在已回退主模型。";
                        }
                    }
                    catch
                    {
                        modelWarning = $"[SUBAGENT_VALIDATION] spawn 参数模型 {spec.Model} 不存在已回退主模型。";
                    }
                }
                if (!string.IsNullOrWhiteSpace(spec.ProviderId))
                    providerId = spec.ProviderId!;
                int subMaxIter = spec.MaxIterations ?? policy.SubMaxIterations;
                subMaxIter = Math.Clamp(subMaxIter, 1, 50);

                var meta = await _sessions
                    .CreateSubSessionAsync(
                        workspaceId,
                        title: null,
                        providerId,
                        modelId,
                        parentSessionId: mainSessionId,
                        agentRole: "sub",
                        subLabel: spec.Label,
                        createdByCallId: spec.CallId,
                        taskPrompt: spec.Task
                    )
                    .ConfigureAwait(false);
                Publish(
                    mainSessionId,
                    "SubAgentsStarted",
                    new
                    {
                        subs = new[]
                        {
                            new
                            {
                                callId = spec.CallId,
                                subId = meta.Id,
                                label = spec.Label
                            }
                        },
                        total = spawnCalls.Count,
                        maxParallel = policy.SubMaxParallel,
                    }
                );
                var subCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt);
                string taskText = spec.Task;
                string subId = meta.Id;
                string lbl = spec.Label;
                var handle = new SubHandle
                {
                    SubSessionId = subId,
                    CallId = spec.CallId,
                    Label = lbl,
                    Cts = subCts,
                    StartedAtUtc = DateTime.UtcNow,
                    Task = Task.Run(
                        () =>
                            RunSubTurnAsync(
                                workspaceId,
                                subId,
                                taskText,
                                providerId,
                                modelId,
                                subMaxIter,
                                policy,
                                modelWarning,
                                subCts.Token
                            ),
                        subCts.Token
                    ),
                };
                var list = _trees.GetOrAdd(mainSessionId, _ => new List<SubHandle>());
                lock (list)
                {
                    list.Add(handle);
                }
                handles.Add(handle);
            }
        }
        catch
        {
            // need22-P1 T2：建子中途失败，已建子取消+摘除+Dispose，不泄漏 CTS，再原样抛出。
            CleanupCreatedHandles(mainSessionId, handles);
            throw;
        }
        return (handles, rejected);
    }

    /// <summary>need22-P1 T2：建子失败清理（已建 handles 取消+摘除+Dispose，不泄漏 CTS）。</summary>
    private void CleanupCreatedHandles(string mainSessionId, List<SubHandle> handles)
    {
        try
        {
            if (_trees.TryGetValue(mainSessionId, out var list))
            {
                lock (list)
                {
                    foreach (var h in handles)
                        list.Remove(h);
                }
            }
        }
        catch { }
        foreach (var h in handles)
        {
            try
            {
                h.Cts.Cancel();
            }
            catch { }
            try
            {
                h.Cts.Dispose();
            }
            catch { }
        }
    }

    private async Task<AgentTurnResult> RunSubTurnAsync(
        string workspaceId,
        string subSessionId,
        string task,
        string providerId,
        string modelId,
        int subMaxIter,
        SubPolicySnapshot policy,
        string? modelWarning,
        CancellationToken subCt
    )
    {
        try
        {
            await SetSubStatusAsync(workspaceId, subSessionId, "running").ConfigureAwait(false);
            Publish(
                subSessionId,
                "SubAgentProgress",
                new { subId = subSessionId, status = "running" }
            );
            var subReg = ToolRoleRegistries.ForSubRole(policy);
            var subOrch = new AgentOrchestrator(
                _sessions,
                _budget,
                _gateway,
                subReg,
                _storageManager,
                _providers,
                _prompts,
                maxIterations: subMaxIter,
                agentConfigService: _agentConfig,
                usageMetrics: _usage,
                subPolicy: policy,
                agentRole: "sub"
            );
            var r = await subOrch
                .RunAgentTurnAsync(workspaceId, subSessionId, task, providerId, modelId, subCt)
                .ConfigureAwait(false);
            await SetSubStatusAsync(
                    workspaceId,
                    subSessionId,
                    r.WasCancelled ? "cancelled" : (r.Success ? "succeeded" : "failed")
                )
                .ConfigureAwait(false);
            Publish(
                subSessionId,
                "SubAgentProgress",
                new
                {
                    subId = subSessionId,
                    status = r.WasCancelled ? "cancelled" : (r.Success ? "succeeded" : "failed")
                }
            );
            return r;
        }
        catch (OperationCanceledException)
        {
            try
            {
                await SetSubStatusAsync(workspaceId, subSessionId, "cancelled")
                    .ConfigureAwait(false);
            }
            catch { }
            return AgentTurnResult.Cancelled(0, 0);
        }
        catch (Exception ex)
        {
            try
            {
                await SetSubStatusAsync(workspaceId, subSessionId, "failed").ConfigureAwait(false);
            }
            catch { }
            return AgentTurnResult.Failed("SUB_EXEC_ERROR", ex.Message);
        }
    }
}
