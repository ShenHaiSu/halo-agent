using System.Collections.Concurrent;
using System.Text.Json;
using Host.Desktop.Bridge;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.Tools;

namespace Host.Desktop.Services;

/// <summary>need13：子代理句柄（内存注册表项）。</summary>
public sealed class SubHandle
{
    public string SubSessionId { get; init; } = string.Empty;
    public string CallId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public Task<AgentTurnResult> Task { get; set; } = System.Threading.Tasks.Task.FromResult(AgentTurnResult.Cancelled(0, 0));
    public CancellationTokenSource Cts { get; init; } = new();
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
}
/// <summary>need13：子代理 spawn 请求（Orchestrator 分流解析产物）。</summary>
public sealed class SpawnCallSpec
{
    public string CallId { get; init; } = string.Empty;
    public string Task { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? ProviderId { get; init; }
    public int? MaxIterations { get; init; }
}

/// <summary>need13：子代理结果（落主历史 Tool(callId, json, "spawn_subagent")）。</summary>
public sealed class SubAgentResult
{
    public string CallId { get; init; } = string.Empty;
    public string SubSessionId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public int ToolExecutions { get; init; }
    public int Iterations { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CachedTokens { get; init; }
    public string? ErrorCode { get; init; }
     public string? ErrorMessage { get; init; }
     public string? Warning { get; init; }
     /// <summary>need22-P1 T4：Barrier 超时标记（主轮继续，超时子按 summary 截断回填）。</summary>
     public bool TimedOut { get; init; }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        subSessionId = SubSessionId,
        label = Label,
        success = Success,
        summary = Summary,
        toolExecutions = ToolExecutions,
        iterations = Iterations,
        usage = new { input = InputTokens, output = OutputTokens, cached = CachedTokens },
        errorCode = ErrorCode,
        errorMessage = ErrorMessage,
        warning = Warning,
        timedOut = TimedOut,
    }, HaloJsonOptions.CompactCamelCase);
}

/// <summary>
/// need13-04：SubAgentCoordinator（只管建会话 + 起 Task + 等 + 取消 + 事件，不管 LLM 文本）。
/// </summary>
public sealed class SubAgentCoordinator
{
    private readonly ConcurrentDictionary<string, List<SubHandle>> _trees = new(StringComparer.Ordinal);
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
        UsageMetricsService? usage = null)
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
    public static (SpawnCallSpec? Spec, string? ValidationFail, string? Warning) ParseSpawnCall(string callId, string argumentsJson)
    {
        string task = string.Empty, label = string.Empty, model = string.Empty, providerId = string.Empty;
        int? maxIter = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。", null);
            if (root.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.String) task = t.GetString() ?? string.Empty;
            if (root.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String) label = l.GetString() ?? string.Empty;
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString() ?? string.Empty;
            if (root.TryGetProperty("providerId", out var p) && p.ValueKind == JsonValueKind.String) providerId = p.GetString() ?? string.Empty;
            if (root.TryGetProperty("maxIterations", out var mi) && mi.ValueKind == JsonValueKind.Number && mi.TryGetInt32(out int v)) maxIter = v;
        }
        catch
        {
            return (null, "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。", null);
        }
        if (string.IsNullOrWhiteSpace(task))
            return (null, "[SUBAGENT_VALIDATION] spawn 参数非法：task 为空。", null);
        string warning = string.Empty;
        string t2 = task.Trim();
        if (t2.Length > 8000) { t2 = t2[..8000]; warning = "[SUBAGENT_VALIDATION] spawn 参数 task 超 8000 字已截断。"; }
        string lb = label.Trim();
        if (lb.Length > 20) lb = lb[..20];
        if (string.IsNullOrEmpty(lb)) lb = t2.Length > 12 ? t2[..12] : t2;
        int? miClamped = maxIter.HasValue ? Math.Clamp(maxIter.Value, 1, 50) : null;
        return (new SpawnCallSpec
        {
            CallId = callId,
            Task = t2,
            Label = lb,
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
            MaxIterations = miClamped,
        }, null, string.IsNullOrEmpty(warning) ? null : warning);
    }

    /// <summary>
    /// 建子：take 前 N 个（N=policy.SubMaxParallel），余下记 quotaRejected（不建会话，直接 Fail 回包）。
    /// </summary>
    public async Task<(List<SubHandle> Handles, List<(string CallId, string FailText)> Rejected)> CreateSubsAsync(
        string workspaceId, string mainSessionId,
        IReadOnlyList<SpawnCallSpec> spawnCalls,
        string mainProviderId, string mainModelId,
        SubPolicySnapshot policy,
        CancellationToken mainCt)
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
                rejected.Add((spec.CallId,
                    $"[SUBAGENT_QUOTA_EXCEEDED] 本轮子代理并发超限（max={policy.SubMaxParallel}，本轮第{i + 1}个）。本次 spawn 已拒绝。请不要重试，先处理已启动的子结果，不足部分拆到下一轮或串行直做。"));
                continue;
            }
            // model 覆盖：存在且 Enabled 否则回退主模型 + warning
            string providerId = mainProviderId, modelId = mainModelId;
            string? modelWarning = null;
            if (!string.IsNullOrWhiteSpace(spec.Model) && _providers != null)
            {
                try
                {
                    var doc = _providers.GetProvidersConfigInternal();
                    var found = doc.Providers.SelectMany(p => p.Models.Select(m => (P: p, M: m)))
                        .FirstOrDefault(x => string.Equals(x.M.Id, spec.Model, StringComparison.OrdinalIgnoreCase));
                    if (found.M != null && found.P.Enabled && found.M.Enabled)
                    {
                        providerId = found.P.Id; modelId = found.M.Id;
                    }
                    else
                    {
                        modelWarning = $"[SUBAGENT_VALIDATION] spawn 参数模型 {spec.Model} 不存在已回退主模型。";
                    }
                }
                catch { modelWarning = $"[SUBAGENT_VALIDATION] spawn 参数模型 {spec.Model} 不存在已回退主模型。"; }
            }
            if (!string.IsNullOrWhiteSpace(spec.ProviderId)) providerId = spec.ProviderId!;
            int subMaxIter = spec.MaxIterations ?? policy.SubMaxIterations;
            subMaxIter = Math.Clamp(subMaxIter, 1, 50);

            var meta = await _sessions.CreateSubSessionAsync(workspaceId, title: null,
                providerId, modelId, parentSessionId: mainSessionId, agentRole: "sub",
                subLabel: spec.Label, createdByCallId: spec.CallId, taskPrompt: spec.Task).ConfigureAwait(false);
            Publish(mainSessionId, "SubAgentsStarted", new
            {
                subs = new[] { new { callId = spec.CallId, subId = meta.Id, label = spec.Label } },
                total = spawnCalls.Count,
                maxParallel = policy.SubMaxParallel,
            });
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
                Task = Task.Run(() => RunSubTurnAsync(workspaceId, subId, taskText, providerId, modelId, subMaxIter, policy, modelWarning, subCts.Token), subCts.Token),
            };
            var list = _trees.GetOrAdd(mainSessionId, _ => new List<SubHandle>());
            lock (list) { list.Add(handle); }
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
                lock (list) { foreach (var h in handles) list.Remove(h); }
            }
        }
        catch { }
        foreach (var h in handles)
        {
            try { h.Cts.Cancel(); } catch { }
            try { h.Cts.Dispose(); } catch { }
        }
    }

    private async Task<AgentTurnResult> RunSubTurnAsync(
        string workspaceId, string subSessionId, string task,
        string providerId, string modelId, int subMaxIter,
        SubPolicySnapshot policy, string? modelWarning, CancellationToken subCt)
    {
        try
        {
            await SetSubStatusAsync(workspaceId, subSessionId, "running").ConfigureAwait(false);
            Publish(subSessionId, "SubAgentProgress", new { subId = subSessionId, status = "running" });
            var subReg = ToolRegistryFactory.ForSub(policy);
            var subOrch = new AgentOrchestrator(_sessions, _budget, _gateway, subReg, _storageManager,
                _providers, _prompts, maxIterations: subMaxIter,
                agentConfigService: _agentConfig, usageMetrics: _usage,
                subPolicy: policy, agentRole: "sub");
            var r = await subOrch.RunAgentTurnAsync(workspaceId, subSessionId, task, providerId, modelId, subCt).ConfigureAwait(false);
            await SetSubStatusAsync(workspaceId, subSessionId, r.WasCancelled ? "cancelled" : (r.Success ? "succeeded" : "failed")).ConfigureAwait(false);
            Publish(subSessionId, "SubAgentProgress", new { subId = subSessionId, status = r.WasCancelled ? "cancelled" : (r.Success ? "succeeded" : "failed") });
            return r;
        }
        catch (OperationCanceledException)
        {
            try { await SetSubStatusAsync(workspaceId, subSessionId, "cancelled").ConfigureAwait(false); } catch { }
            return AgentTurnResult.Cancelled(0, 0);
        }
        catch (Exception ex)
        {
            try { await SetSubStatusAsync(workspaceId, subSessionId, "failed").ConfigureAwait(false); } catch { }
            return AgentTurnResult.Failed("SUB_EXEC_ERROR", ex.Message);
        }
    }

    /// <summary>need22-P1 T4：Barrier 总体超时（默认=子数×单子超时+buffer；超时子截断+timedOut:true，主轮继续；响应 mainCt 取消）。</summary>
    public async Task<List<SubAgentResult>> BarrierAsync(
        string workspaceId, string parentSessionId,
        IReadOnlyList<SubHandle> handles,
        IReadOnlyList<(string CallId, string FailText)> rejected,
        CancellationToken mainCt,
        TimeSpan? overallTimeout = null)
    {
        var rejectMap = rejected.ToDictionary(x => x.CallId, x => x.FailText, StringComparer.Ordinal);
        TimeSpan budget = overallTimeout ?? ResolveBarrierBudget(handles.Count);
        var timedOutHandles = new HashSet<SubHandle>();
        if (handles.Count > 0)
        {
            Publish(parentSessionId, "SubAgentsBarrier", new { total = handles.Count, waiting = true });
            Task allTask = Task.WhenAll(handles.Select(h => h.Task));
            Task first = await Task.WhenAny(allTask, Task.Delay(budget, mainCt)).ConfigureAwait(false);
            mainCt.ThrowIfCancellationRequested();
            if (first != allTask)
            {
                // 总体超时：掐断未完成子，标记 timedOut，主轮继续（残留任务由其 CTS 取消后自行收敛，CTS 在方法末尾统一 Dispose）。
                foreach (var h in handles)
                {
                    if (!h.Task.IsCompleted)
                    {
                        timedOutHandles.Add(h);
                        try { h.Cts.Cancel(); } catch { }
                    }
                }
                Publish(parentSessionId, "SubAgentsBarrierTimeout", new { total = handles.Count, timedOut = timedOutHandles.Count, budgetMs = (long)budget.TotalMilliseconds });
            }
            else
            {
                try { await allTask.ConfigureAwait(false); } catch { /* 单 handle 归因在下环处理 */ }
            }
        }
        var results = new List<SubAgentResult>();
        foreach (var h in handles)
        {
            mainCt.ThrowIfCancellationRequested();
            AgentTurnResult r;
            bool timedOut = timedOutHandles.Contains(h);
            if (timedOut && !h.Task.IsCompleted)
            {
                // 超时残留子不再等待（已发 Cancel）：异常由延续观察，Barrier 直接截断回填，主轮继续。
                _ = h.Task.ContinueWith(t => { try { var _ = t.Exception; } catch { } },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                var (pin, pout, pcached) = await ReadTotalUsageAsync(workspaceId, h.SubSessionId).ConfigureAwait(false);
                string psummary = await ReadLastAssistantTextAsync(workspaceId, h.SubSessionId).ConfigureAwait(false);
                results.Add(new SubAgentResult
                {
                    CallId = h.CallId,
                    SubSessionId = h.SubSessionId,
                    Label = h.Label,
                    Success = false,
                    Summary = TruncateSummary(psummary, 2000) + "\n[子超时截断：Barrier 总体超时，主轮已继续，残缺结果仅供参考。]",
                    ToolExecutions = 0,
                    Iterations = 0,
                    InputTokens = pin,
                    OutputTokens = pout,
                    CachedTokens = pcached,
                    ErrorCode = "SUB_TIMEOUT",
                    ErrorMessage = $"[SUB_TIMEOUT] 子 {h.Label} 超出 Barrier 总体超时，已截断，主轮继续。",
                    TimedOut = true,
                });
                continue;
            }
            try { r = await h.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (mainCt.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { r = AgentTurnResult.Cancelled(0, 0); }
            catch (Exception ex) { r = AgentTurnResult.Failed("SUB_EXEC_ERROR", ex.Message); }
            string summary = await ReadLastAssistantTextAsync(workspaceId, h.SubSessionId).ConfigureAwait(false);
            // need22-P1 T7：累加子会话全部消息用量（多轮子耗全量，非末条）。
            var (inp, outp, cached) = await ReadTotalUsageAsync(workspaceId, h.SubSessionId).ConfigureAwait(false);
            if (timedOut)
            {
                // need22-P1 T4：超时子按 summary 截断回填 + timedOut:true，主轮继续。
                results.Add(new SubAgentResult
                {
                    CallId = h.CallId,
                    SubSessionId = h.SubSessionId,
                    Label = h.Label,
                    Success = false,
                    Summary = TruncateSummary(summary, 2000) + "\n[子超时截断：Barrier 总体超时，主轮已继续，残缺结果仅供参考。]",
                    ToolExecutions = r.ToolExecutions,
                    Iterations = r.Iterations,
                    InputTokens = inp,
                    OutputTokens = outp,
                    CachedTokens = cached,
                    ErrorCode = "SUB_TIMEOUT",
                    ErrorMessage = $"[SUB_TIMEOUT] 子 {h.Label} 超出 Barrier 总体超时，已截断，主轮继续。",
                    TimedOut = true,
                });
                continue;
            }
            string? errCode = null, errMsg = null;
            if (r.WasCancelled) { errCode = "SUBAGENT_CANCELLED"; errMsg = $"[SUBAGENT_CANCELLED] 子 {h.Label} 已随主取消被掐断（用户取消）。残缺结果不回填。"; }
            else if (r.WasTruncated) { errCode = "TURN_TRUNCATED"; errMsg = "子轮次耗尽被截断。"; }
            else if (!r.Success) { errCode = r.ErrorCode ?? "STREAM_ERROR"; errMsg = r.ErrorMessage; }
            results.Add(new SubAgentResult
            {
                CallId = h.CallId,
                SubSessionId = h.SubSessionId,
                Label = h.Label,
                Success = r.Success && !r.WasTruncated,
                Summary = summary,
                ToolExecutions = r.ToolExecutions,
                Iterations = r.Iterations,
                InputTokens = inp,
                OutputTokens = outp,
                CachedTokens = cached,
                ErrorCode = r.Success && !r.WasTruncated ? null : errCode,
                ErrorMessage = r.Success && !r.WasTruncated ? null : errMsg,
            });
        }
        Publish(parentSessionId, "SubAgentsBarrierReleased", new
        {
            subs = results.Select(x => new { callId = x.CallId, subId = x.SubSessionId, success = x.Success, timedOut = x.TimedOut }).ToArray(),
            billedPrompt = results.Sum(x => x.InputTokens),
            billedCompletion = results.Sum(x => x.OutputTokens),
            timedOut = results.Count(x => x.TimedOut),
        });
        var list = _trees.GetOrAdd(parentSessionId, _ => new List<SubHandle>());
        lock (list) { foreach (var h in handles) list.Remove(h); }
        // need22-P1 T2：Barrier 结束统一释放子 CTS（已从树摘除；运行中残留已先 Cancel，Dispose 不影响其 Token 查询）。
        foreach (var h in handles) { try { h.Cts.Dispose(); } catch { } }
        return results;
    }

    /// <summary>need22-P1 T4：Barrier 总体超时预算（子数×单子超时+buffer；单子超时取 turnTimeoutMs）。</summary>
    private TimeSpan ResolveBarrierBudget(int subCount)
    {
        try
        {
            if (_agentConfig != null && subCount > 0)
            {
                var cfg = _agentConfig.GetInternal();
                long single = cfg.TurnTimeoutMs > 0 ? cfg.TurnTimeoutMs : Host.Desktop.Core.Models.AgentDefaults.TurnTimeoutMs;
                long buffer = Math.Max(0, cfg.SubBarrierBufferMs);
                long total = checked((long)subCount * single + buffer);
                return TimeSpan.FromMilliseconds(Math.Clamp(total, 1000L, (long)Host.Desktop.Core.Models.AgentDefaults.HardTimeoutCeilMs * 8));
            }
        }
        catch { /* 配置读取失败回默认预算 */ }
        return TimeSpan.FromMilliseconds(Math.Clamp(
            (long)Math.Max(1, subCount) * Host.Desktop.Core.Models.AgentDefaults.TurnTimeoutMs + Host.Desktop.Core.Models.AgentDefaults.SubBarrierBufferMs,
            1000L, (long)Host.Desktop.Core.Models.AgentDefaults.HardTimeoutCeilMs * 8));
    }

    private static string TruncateSummary(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length > max ? s.Substring(0, max) : s;
    }

    public IReadOnlyList<SubHandle> GetActive(string parentSessionId)
    {
        if (_trees.TryGetValue(parentSessionId, out var list))
        {
            lock (list) { return list.ToList(); }
        }
        return Array.Empty<SubHandle>();
    }

    /// <summary>need13：级联取消（先子后主由 Bridge 层组装；此处只掐子 CTS）。</summary>
    public int CancelTree(string parentSessionId)
    {
        int n = 0;
        if (_trees.TryGetValue(parentSessionId, out var list))
        {
            List<SubHandle> snap;
            lock (list) { snap = list.ToList(); }
            foreach (var h in snap)
            {
                try { h.Cts.Cancel(); n++; } catch { }
            }
        }
        return n;
    }

    public bool CancelSub(string parentSessionId, string subSessionId)
    {
        if (_trees.TryGetValue(parentSessionId, out var list))
        {
            SubHandle? h;
            lock (list) { h = list.FirstOrDefault(x => string.Equals(x.SubSessionId, subSessionId, StringComparison.OrdinalIgnoreCase)); }
            if (h != null) { try { h.Cts.Cancel(); return true; } catch { return false; } }
        }
        return false;
    }

    /// <summary>need13：启动自愈（running 残留置 cancelled + 落关闭 notice）。</summary>
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        int n = 0;
        try
        {
            var workspaces = await _sessions.GetAllWorkspacesAsync(ct).ConfigureAwait(false);
            foreach (var ws in workspaces)
            {
                List<SessionMetadata> all;
                try
                {
                    // 读全量 index（含子）：经 GetSubSessions 逐主扫太贵，直接走仓储由 Service 侧补接口前先用 GetSessionsByWorkspaceAsync + 兜底
                    all = await _sessions.GetSessionsByWorkspaceAsync(ws.Id, ct).ConfigureAwait(false);
                }
                catch { continue; }
                foreach (var m in all)
                {
                    if (!string.Equals(m.AgentRole, "sub", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(m.SubStatus, "running", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(m.SubStatus, "pending", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        m.SubStatus = "cancelled";
                        m.UpdatedAtUtc = DateTime.UtcNow;
                        await _sessions.AppendSessionMessageAsync(ws.Id, m.Id,
                            UnifiedChatMessage.Assistant("[系统恢复] 该子代理因宿主重启已终止，未完成部分请由主重新 spawn。"), CancellationToken.None).ConfigureAwait(false);
                        n++;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return n;
    }

    private async Task SetSubStatusAsync(string workspaceId, string subSessionId, string status)
    {
        try
        {
            var meta = await _sessions.GetSessionAsync(workspaceId, subSessionId).ConfigureAwait(false);
            if (meta == null) return;
            meta.SubStatus = status;
            meta.UpdatedAtUtc = DateTime.UtcNow;
            await _sessions.AppendSessionMessageAsync(workspaceId, subSessionId,
                UnifiedChatMessage.Assistant($"[SubStatus] {status}"), CancellationToken.None).ConfigureAwait(false);
            // Append 已刷 index（含 SubStatus）；无需额外写 meta
        }
        catch { }
    }

    private async Task<string> ReadLastAssistantTextAsync(string workspaceId, string subSessionId)
    {
        try
        {
            var msgs = await _sessions.GetSessionMessagesAsync(workspaceId, subSessionId).ConfigureAwait(false);
            for (int i = msgs.Count - 1; i >= 0; i--)
            {
                var m = msgs[i];
                if (!string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
                bool hasCalls = m.ToolCalls != null && m.ToolCalls.Count > 0;
                if (hasCalls) continue;
                if (!string.IsNullOrWhiteSpace(m.Content)) return m.Content!;
            }
            var last = msgs.LastOrDefault();
            return (last?.Content ?? string.Empty) + "\n[子未输出总结]";
        }
        catch { return "[子未输出总结]"; }
    }

    /// <summary>need22-P1 T7：累加子会话全部消息用量（多轮子耗全量；替代只读末条 usage 的旧口径）。</summary>
    private async Task<(long In, long Out, long Cached)> ReadTotalUsageAsync(string workspaceId, string subSessionId)
    {
        try
        {
            var msgs = await _sessions.GetSessionMessagesAsync(workspaceId, subSessionId).ConfigureAwait(false);
            long inp = 0, outp = 0, cached = 0;
            foreach (var m in msgs)
            {
                inp += m.InputTokens ?? 0;
                outp += m.OutputTokens ?? 0;
                cached += m.CachedTokens ?? 0;
            }
            return (inp, outp, cached);
        }
        catch { return (0, 0, 0); }
    }

    private static void Publish(string sessionId, string kind, object payload)
    {
        try
        {
            JsEventDispatcher.Publish("agent:turn-event", new { sessionId, kind, payload, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }
        catch { }
    }
}
 /// <summary>need16：子用量快照复用（Bridge 详情与 Coordinator 共用）。</summary>
 public static class SubStatsReader
 {
     public static async Task<(long In, long Out, long Cached, int Msgs, int Tools, int Iters)> ReadStatsAsync(IWorkspaceSessionService sessions, string workspaceId, string subSessionId)
     {
         try
         {
             var msgs = await sessions.GetSessionMessagesAsync(workspaceId, subSessionId).ConfigureAwait(false);
             var last = msgs.LastOrDefault(m => m.InputTokens.HasValue && m.InputTokens.Value > 0);
             long inp = last?.InputTokens ?? 0; long outp = 0; long cached = last?.CachedTokens ?? 0;
             int tools = 0; int iters = 0;
             foreach (var m in msgs)
             {
                 if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)) tools++;
                 if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                 {
                     outp += m.OutputTokens ?? 0;
                     if (m.ToolCalls != null) iters++;
                     else if (!string.IsNullOrWhiteSpace(m.Content)) iters++;
                 }
             }
             return (inp, outp, cached, msgs.Count, tools, Math.Max(iters, msgs.Count(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))));
         }
         catch { return (0, 0, 0, 0, 0, 0); }
     }
 }
