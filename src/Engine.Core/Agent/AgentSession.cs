using System.Text.Json;
using HaloAgent.Engine.Abstractions;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent.Prompts;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Permissions;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Core.Util;
using HaloAgent.Engine.Storage.Config;
using HaloAgent.Engine.Storage.Sessions;
using HaloAgent.Engine.Storage.Undo;

namespace HaloAgent.Engine.Core.Agent;

/// <summary>一次待答询问的内部表示（含回答通道）。</summary>
public sealed class PendingAskSlot
{
    private readonly TaskCompletionSource<AskAnswer> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingAsk Ask { get; init; } = new PendingAsk();
    public Task<AskAnswer> Answer => _tcs.Task;

    public void Resolve(bool allow, bool always) =>
        _tcs.TrySetResult(new AskAnswer(allow, always));

    public void Cancel() => _tcs.TrySetCanceled();
}

public sealed record AskAnswer(bool Allow, bool Always);

/// <summary>
/// agent 会话：回合循环 + 工具执行 + 权限 + 全部边界机制。
/// 七个机制：回合 / 轮次预算 / 工具逐个执行 / 中断补占位 /
/// 排队轮边界注入 / 重复刹车 / 弱网重试。
/// </summary>
public sealed class AgentSession : IToolHost, IAsyncDisposable
{
    // ---------- 依赖 ----------
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly SessionStore _store;
    private readonly EventBus _bus;
    private readonly IToolHost _self;
    private readonly string _dataDir;

    // ---------- 状态 ----------
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();
    private readonly Queue<string> _inbox = new();

    private string _cwd;
    private PermissionMode _mode;
    private string _persona;
    private int _maxTurns;
    private int _retry;
    private bool _askInteractive;
    private string? _globalProxyUrl;
    private string _activeProviderId = "";
    private string _activeProviderDisplay = "";
    private string _activeModelId = "";
    private string _activeModelDisplay = "";
    private string _activeProtocol = "chat";
    private int? _activeContextWindow;
    private int? _activeMaxTokens;
    private bool _activeAcceptsImages;
    private bool _activeIncludeReasoning = true;
    private bool _activeProxyInUse;
    private string _activeBaseUrl = "";
    private bool _activeApiKeySet;
    private bool _activeConfigured;
    private bool _fileAlways;
    private bool _commandAlways;

    private CancellationTokenSource? _turnCts;
    private volatile bool _busy;
    private string? _currentTurnId;
    private string? _currentToolCallId;
    private PendingAskSlot? _pendingAsk;
    private int _askSeq;

    private long _usageIn;
    private long _usageOut;
    private long? _lastPromptTokens;
    private int _roundUsed;
    private int _roundLimit;
    private string? _lastResponseId;
    private int _responsesSentCount;

    private const int RepeatWarn = 3;
    private const int RepeatAbort = 6;
    private const int ContextMaxChars = 20_000;
    private const int KeepTailForCompact = 4;


    public AgentSession(
        ILlmClient llm,
        ToolRegistry tools,
        SessionStore store,
        EventBus bus,
        string dataDir,
        string cwd,
        PermissionMode mode,
        string persona,
        int maxTurns,
        int retry,
        bool askInteractive)
    {
        _llm = llm;
        _tools = tools;
        _store = store;
        _bus = bus;
        _self = this;
        _dataDir = dataDir;
        _cwd = cwd;
        _mode = mode;
        _persona = persona;
        _maxTurns = maxTurns;
        _retry = retry;
        _askInteractive = askInteractive;

        PushSystemPrompt();
    }

    // ============================================================
    //  状态访问
    // ============================================================

    public bool IsBusy => _busy;
    public string SessionId => _store.SessionId;
    public string? HistoryPath => _store.HistoryPath;
    public string Cwd => _cwd;
    public PermissionMode Mode => _mode;
    public string Persona => _persona;
    public int MaxTurns => _maxTurns;
    public int Retry => _retry;
    public string? CurrentToolCallId => _currentToolCallId;

    /// <summary>当前生效的网络代理（P10：全局代理，与 provider.useProxy 无关，供 WebFetch 等非模型链路用）。</summary>
    public string? ProxyUrl => _globalProxyUrl;
    public string DataDir => _dataDir;

    /// <summary>P10 热切：持久化 active 指针后同进程换 llm（无需重启）。busy 时由调用方前置拒绝。</summary>
    public void ReconfigureActive(ResolvedActiveModel resolved)
    {
        var proto = (resolved.Protocol ?? "chat").ToLowerInvariant() == "responses"
            ? LlmProtocol.Responses : LlmProtocol.Chat;
        _llm.Reconfigure(new LlmConfig
        {
            BaseUrl = resolved.BaseUrl,
            Model = resolved.ModelId,
            ApiKey = resolved.ApiKey,
            ProxyUrl = resolved.ProxyUrl,
            Headers = resolved.Headers,
            Protocol = proto,
            MaxTokens = resolved.MaxTokens,
            AcceptsImages = resolved.AcceptsImages,
            IncludeReasoningHistory = resolved.IncludeReasoningHistory,
        });
        string oldProto, oldModel, oldProv;
        lock (_lock) { oldProto = _activeProtocol; oldModel = _activeModelId; oldProv = _activeProviderId; }
        lock (_lock)
        {
            _globalProxyUrl = resolved.GlobalProxyUrl;
            _activeProviderId = resolved.ProviderId ?? "";
            _activeProviderDisplay = resolved.ProviderDisplay ?? "";
            _activeModelId = resolved.ModelId ?? "";
            _activeModelDisplay = resolved.ModelDisplay ?? "";
            _activeProtocol = resolved.Protocol ?? "chat";
            _activeContextWindow = resolved.ContextWindow;
            _activeMaxTokens = resolved.MaxTokens;
            _activeAcceptsImages = resolved.AcceptsImages;
            _activeIncludeReasoning = resolved.IncludeReasoningHistory;
            _activeProxyInUse = resolved.ProxyInUse;
            _activeBaseUrl = resolved.BaseUrl ?? "";
            _activeApiKeySet = !string.IsNullOrEmpty(resolved.ApiKey);
            _activeConfigured = resolved.Configured;
            _maxTurns = resolved.MaxTurns;
            _retry = resolved.Retry;
            _persona = resolved.Persona ?? "";
            if (_messages.Count > 0 && _messages[0].Role == Role.System)
                _messages[0] = ChatMessage.System(SystemPromptBuilder.Build(
                    _cwd, _mode, _store.SessionId, _persona, _dataDir));
        }
        if (!string.Equals(oldProto, resolved.Protocol ?? "chat", StringComparison.OrdinalIgnoreCase) || !string.Equals(oldModel, resolved.ModelId ?? "", StringComparison.Ordinal) || !string.Equals(oldProv, resolved.ProviderId ?? "", StringComparison.Ordinal))
            ClearResponseChain();
    }

    /// <summary>设置全局代理（非模型链路用）。</summary>
    public void SetGlobalProxy(string? url)
    {
        lock (_lock) _globalProxyUrl = url;
    }
     /// <summary>P12-02：保存后 active 悬空/非法时显式置未配置（此前无此方法，只能保持旧内存）。</summary>
     public void ClearActive()
     {
         lock (_lock)
         {
             _activeProviderId = "";
             _activeProviderDisplay = "";
             _activeModelId = "";
             _activeModelDisplay = "";
             _activeProtocol = "chat";
             _activeContextWindow = null;
             _activeMaxTokens = null;
             _activeAcceptsImages = false;
             _activeIncludeReasoning = true;
             _activeProxyInUse = false;
             _activeBaseUrl = "";
             _activeApiKeySet = false;
             _activeConfigured = false;
         }
         ClearResponseChain();
     }
    /// <summary>P13 链管理：清链（内存 + 落盘同步）。</summary>
    public void ClearResponseChain()
    {
        lock (_lock) { _lastResponseId = null; _responsesSentCount = 0; }
        try { _store.SaveResponseId(null); } catch { }
    }
    private void SetResponseId(string? id)
    {
        lock (_lock) { _lastResponseId = id; }
        try { _store.SaveResponseId(id); } catch { }
    }
     /// <summary>P12-02：setActive 成功后经存量 notice 事件提示（不新增事件 kind，不改契约）。</summary>
     public async Task PublishAgentChangedAsync(string text, CancellationToken ct)
     {
         try
         {
             await _bus.PublishStateAsync(EventKinds.Notice,
                 new NoticePayload { Text = text, Level = "info" }, ct).ConfigureAwait(false);
         }
         catch { /* 推送失败不影响切换结果 */ }
     }


    public void SetMode(PermissionMode mode)
    {
        lock (_lock)
        {
            _mode = mode;
            RefreshSystemPrompt();
        }
    }

    public void SetPersona(string name)
    {
        lock (_lock)
        {
            _persona = name;
            RefreshSystemPrompt();
        }
    }

    public void SetMaxTurns(int value) => _maxTurns = Math.Max(0, value);
    public void SetRetry(int value) => _retry = Math.Clamp(value, 0, 8);

     public void SetCwd(string path)
     {
         if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不能为空");
         if (path.Contains('\0')) throw new ArgumentException("path 含非法字符");
         string full;
         lock (_lock) full = ToolPaths.Resolve(_cwd, path);
         // GetFullPath 非法输入直接上抛（RealMethods 转 paramsInvalid），见 05。
         ThrowIfProtectedCwd(full);
         if (!Directory.Exists(full)) throw new DirectoryNotFoundException("目录不存在: " + path);
         lock (_lock)
         {
             _cwd = full;
             _fileAlways = false;
             _commandAlways = false;
             RefreshSystemPrompt();
         }
         try { _store.UpdateCwdAtomic(full); } catch { }
         _ = _bus.PublishStateAsync(EventKinds.Notice,
             new NoticePayload { Text = "已切换工作目录，之前的“总是允许”已失效", Level = "info" },
             CancellationToken.None);
         _ = PublishSessionInfoAsync(CancellationToken.None);
     }

     /// <summary>P10-fix3 E：拒绝裸盘符根与系统目录做 cwd（与 ToolPaths.ResolveChecked 同口径）。</summary>
     internal static void ThrowIfProtectedCwd(string full)
     {
         if (full.Length is 2 or 3 && full.Length >= 2 && full[1] == ':')
             throw new ArgumentException("拒绝将工作目录设到此处: " + full);
         try
         {
             var root = Path.GetPathRoot(full);
             if (!string.IsNullOrEmpty(root) && string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
                 throw new ArgumentException("拒绝将工作目录设到此处: " + full);
         }
         catch (ArgumentException) { throw; }
         catch { }
         var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
         if (!string.IsNullOrEmpty(sysRoot) &&
             full.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase))
             throw new ArgumentException("拒绝将工作目录设到此处: " + full);
     }

     /// <summary>P10-fix3 C：公开广播（RealMethods 不直接调私有方法）。</summary>
     public void PublishSessionInfo()
     {
         _ = PublishSessionInfoAsync(CancellationToken.None);
     }



    /// <summary>新的 turnId（供 agent.prompt 立即返回）。</summary>
    public string NewTurnId() => "t-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>取消当前回合。</summary>
    public bool CancelTurn()
    {
        var cts = _turnCts;
        if (cts is null) return false;
        try { cts.Cancel(); } catch { }
        _pendingAsk?.Cancel();
        return true;
    }

    // ============================================================
    //  排队消息
    // ============================================================

    public int Enqueue(string text)
    {
        lock (_lock)
        {
            _inbox.Enqueue(text);
            return _inbox.Count;
        }
    }

    public bool TryDequeue(string text)
    {
        lock (_lock)
        {
            if (_inbox.Count == 0) return false;
            var items = _inbox.ToArray();
            var idx = Array.IndexOf(items, text);
            if (idx < 0) return false;

            _inbox.Clear();
            for (int i = 0; i < items.Length; i++)
                if (i != idx) _inbox.Enqueue(items[i]);
            return true;
        }
    }

    public string? TryDrain()
    {
        lock (_lock)
            return _inbox.Count > 0 ? _inbox.Dequeue() : null;
    }

    public IReadOnlyList<string> QueueSnapshot()
    {
        lock (_lock) return _inbox.ToArray();
    }

    // ============================================================
    //  询问（权限 / 轮次）
    // ============================================================

    public PendingAsk? PendingAsk => _pendingAsk?.Ask;

    /// <summary>回答询问。返回 (accepted, reason)。</summary>
    public (bool accepted, string? reason) AnswerAsk(string askId, bool allow, bool always)
    {
        var slot = _pendingAsk;
        if (slot is null) return (false, "no-pending");
        if (slot.Ask.AskId != askId) return (false, "stale");

        // 安全政策：warning 存在时忽略 always（二次降级）
        var effectiveAlways = always && slot.Ask.Warning is null;
        slot.Resolve(allow, effectiveAlways);
        _pendingAsk = null;
        return (true, null);
    }

    private async Task<AskAnswer> AskPermissionAsync(PermissionAsk req, CancellationToken ct)
    {
        var ask = PendingAsk.Permission(
            askId: "q" + (++_askSeq).ToString(),
            kind: req.Kind,
            title: req.Title,
            body: req.Body,
            warning: req.Warning,
            allowAlways: req.AllowAlways);

        var slot = new PendingAskSlot { Ask = ask };
        _pendingAsk = slot;

        await _bus.PublishStateAsync(
            ask.Kind == "rounds" ? EventKinds.AskRounds : EventKinds.AskPermission,
            ask, ct).ConfigureAwait(false);

        try
        {
            using var reg = ct.Register(slot.Cancel);
            var answer = await slot.Answer.ConfigureAwait(false);
            return answer;
        }
        catch (OperationCanceledException)
        {
            return new AskAnswer(false, false);
        }
        finally
        {
            if (ReferenceEquals(_pendingAsk, slot)) _pendingAsk = null;
        }
    }

    public sealed record PermissionAsk
    {
        public string Kind { get; init; } = "command";
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public string? Warning { get; init; }
        public bool AllowAlways { get; init; }
    }

    // ============================================================
    //  主循环：RunTurnAsync
    // ============================================================

    public async Task<TurnResult> RunTurnAsync(string prompt, CancellationToken externalCt)
    {
        if (_busy) throw new InvalidOperationException("已有回合在执行");

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _turnCts = turnCts;
        _busy = true;
        _currentTurnId = NewTurnId();
        var turnId = _currentTurnId;

        try
        {
            return await RunTurnInnerAsync(prompt, turnId, turnCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PublishBusyAsync(false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await _bus.PublishStateAsync(EventKinds.Notice,
                new NoticePayload { Text = "⚠ 请求失败: " + ex.Message, Level = "error" },
                CancellationToken.None).ConfigureAwait(false);

            await _bus.PublishStateAsync(EventKinds.TurnFinished,
                new TurnFinishedPayload { TurnId = turnId, Text = "", Truncated = false, Reason = ex.Message },
                CancellationToken.None).ConfigureAwait(false);

            await PublishBusyAsync(false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _busy = false;
            _currentTurnId = null;
            _turnCts = null;
            _currentToolCallId = null;
        }
    }

    private async Task<TurnResult> RunTurnInnerAsync(
        string prompt, string turnId, CancellationToken ct)
    {
        // ---------- ① @引用展开 ----------
        var (expanded, missing) = AtRefExpander.Expand(prompt, _cwd);
        if (missing.Count > 0)
        {
            await _bus.PublishStateAsync(EventKinds.Notice,
                new NoticePayload
                {
                    Text = "未找到引用: " + string.Join("、", missing) + "（已按原文保留）",
                    Level = "warn",
                }, ct).ConfigureAwait(false);
        }

        // ---------- ② 记录用户消息 + turn.started ----------
        Record(ChatMessage.User(expanded));
        await _bus.PublishStateAsync(EventKinds.TurnStarted,
            new TurnStartedPayload { TurnId = turnId }, ct).ConfigureAwait(false);
        await PublishBusyAsync(true, ct).ConfigureAwait(false);

        // ---------- ③ 工具定义 ----------
        var tools = _tools.ToToolDefs();

        // ---------- ④ 轮次预算 ----------
        int batch = _maxTurns == 0 ? int.MaxValue : _maxTurns;
        long budget = batch;
        int used = 0;
        _roundUsed = 0;
        _roundLimit = _maxTurns;

        // ---------- ⑤ 重复刹车 ----------
        var repeatCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        int toolCallCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // ===== 预算检查 =====
            if (used >= budget)
            {
                var cont = await AskContinueRoundsAsync(used, ct).ConfigureAwait(false);
                if (!cont)
                {
                    var text = _askInteractive
                        ? "⚠ 已达 " + used + " 轮上限，按你的选择停在这里。要接着干就再发一条消息；"
                          + "想一次跑更久可用 --max-turns N 调大上限（--max-turns 0 = 不限）"
                        : "⚠ 已达 " + used + " 轮上限，任务可能没干完。用 --max-turns N 调大上限，"
                          + "--max-turns 0 表示不限";

                    await FinishAsync(turnId, text, truncated: true, ct).ConfigureAwait(false);
                    return new TurnResult(text, toolCallCount, true, _usageIn, _usageOut);
                }
                budget = Math.Min(budget + batch, int.MaxValue);
            }

            used++;
            _roundUsed = used;
            await _bus.PublishStateAsync(EventKinds.RoundStarted,
                new RoundStartedPayload
                {
                    Used = used,
                    Limit = _maxTurns == 0 ? 0 : (int)budget,
                }, ct).ConfigureAwait(false);

            // ===== 调模型（含弱网重试，D2=B 按协议显式分流）=====
            bool isResponses;
            lock (_lock) isResponses = string.Equals(_activeProtocol, "responses", StringComparison.OrdinalIgnoreCase);
            var reply = isResponses ? await ResponsesWithRetryAsync(tools, ct).ConfigureAwait(false) : await ChatWithRetryAsync(tools, ct).ConfigureAwait(false);

            // ===== 累积用量 =====
            if (!reply.Usage.IsEmpty)
            {
                _usageIn += reply.Usage.PromptTokens;
                _usageOut += reply.Usage.CompletionTokens;
                _lastPromptTokens = reply.Usage.PromptTokens;

                await _bus.PublishStateAsync(EventKinds.Usage,
                    new UsagePayload
                    {
                        Prompt = reply.Usage.PromptTokens,
                        Completion = reply.Usage.CompletionTokens,
                        Dropped = _bus.ConsumeDroppedCount() > 0 ? true : null,
                    }, ct).ConfigureAwait(false);
            }

            // ===== 记录助手消息 =====
            Record(ToChatMessage(reply, interrupted: false));

            // ===== 无工具调用 → 回合结束 =====
            if (reply.ToolCalls.Count == 0)
            {
                var text = reply.Content ?? reply.ReasoningContent ?? "(模型无文本输出)";
                await FinishAsync(turnId, text, truncated: false, ct).ConfigureAwait(false);
                return new TurnResult(text, toolCallCount, false, _usageIn, _usageOut);
            }

            // ===== 逐个执行工具 =====
            for (int i = 0; i < reply.ToolCalls.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    // I-3：给剩余 tool_calls 补占位
                    for (int j = i; j < reply.ToolCalls.Count; j++)
                        Record(ChatMessage.Tool(reply.ToolCalls[j].Id, HistoryRepair.PlaceholderToolReply));

                    await FinishAsync(turnId, "⏹ 生成已中断", truncated: true, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new TurnResult("⏹ 生成已中断", toolCallCount, true, _usageIn, _usageOut);
                }

                var tc = reply.ToolCalls[i];
                toolCallCount++;

                var output = await ExecuteToolCallAsync(tc, ct).ConfigureAwait(false);

                // ===== 重复刹车：提醒 =====
                var sig = tc.Function.Name + "|" + tc.Function.Arguments;
                repeatCounts.TryGetValue(sig, out var count);
                repeatCounts[sig] = ++count;

                if (count >= RepeatWarn && count < RepeatAbort)
                {
                    output += "\n\n⚠ 同一个调用（工具 + 参数完全一样）你已经重复第 " + count + " 次了，"
                              + "结果不会变。换参数/换工具/换思路，或直接汇报现状与卡点；"
                              + "再重复到 " + RepeatAbort + " 次本轮会被中止。";
                }

                Record(ChatMessage.Tool(tc.Id, output));

                if (ct.IsCancellationRequested)
                {
                    for (int j = i + 1; j < reply.ToolCalls.Count; j++)
                        Record(ChatMessage.Tool(reply.ToolCalls[j].Id, HistoryRepair.PlaceholderToolReply));

                    await FinishAsync(turnId, "⏹ 生成已中断", truncated: true, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new TurnResult("⏹ 生成已中断", toolCallCount, true, _usageIn, _usageOut);
                }
            }

            // ===== I-2：轮边界插入排队消息 =====
            var injected = TryDrain();
            if (injected is not null)
            {
                repeatCounts.Clear();   // 新意图 → 重置重复计数
                Record(ChatMessage.User(injected));
                await _bus.PublishStateAsync(EventKinds.UserInjected,
                    new UserInjectedPayload { Text = injected }, ct).ConfigureAwait(false);
            }

            // ===== 重复刹车：中止 =====
            var worst = repeatCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (worst.Value >= RepeatAbort)
            {
                var toolName = worst.Key.Split('|')[0];
                var text = "⚠ 检测到原地打转：工具 `" + toolName + "` 用完全相同的参数重复了 " + worst.Value + " 次，"
                           + "已中止本轮。换个思路再来，或直接告诉我要什么。";
                await FinishAsync(turnId, text, truncated: true, ct).ConfigureAwait(false);
                return new TurnResult(text, toolCallCount, true, _usageIn, _usageOut);
            }
        }
    }

    // ============================================================
    //  模型调用（含重试）
    // ============================================================

    private async Task<AssistantReply> ChatWithRetryAsync(
        List<object> tools, CancellationToken ct)
    {
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            AssistantReply reply;
            try
            {
                reply = await _llm.ChatStreamAsync(
                    MessagesSnapshot(),
                    tools,
                    evt =>
                    {
                        switch (evt)
                        {
                            case StreamEvent.TextDelta td:
                                _bus.PublishDelta(EventKinds.TextDelta, td.Text);
                                break;
                            case StreamEvent.ReasoningDelta rd:
                                _bus.PublishDelta(EventKinds.ReasoningDelta, rd.Text);
                                break;
                            default:
                                break;
                        }
                    },
                    ct, _store.SessionId).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _bus.FlushNow();
                throw;
            }
            catch (Exception ex)
            {
                var reason = RetryPolicy.GetRetryReason(ex);
                if (reason is null || attempt >= _retry) throw;

                attempt++;
                await HandleRetryAsync(attempt, reason, ct).ConfigureAwait(false);
                continue;
            }

            // 空回复也重试（几乎总是网络问题）
            if (RetryPolicy.IsEmptyReply(reply))
            {
                if (attempt >= _retry) return reply;   // 不重试了：交给上层当"无输出"处理

                attempt++;
                await HandleRetryAsync(attempt, "空回复（模型没吐任何内容）", ct).ConfigureAwait(false);
                continue;
            }

            _bus.FlushNow();   // 保证最后一个 token 不丢
            return reply;
        }
    }
    private async Task<AssistantReply> ResponsesWithRetryAsync(List<object> chatTools, CancellationToken ct)
    {
        int attempt = 0;
        bool rebuilt = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? prev;
            List<ChatMessage> snap;
            bool includeReasoning;
            bool acceptsImages;
            lock (_lock) { prev = _lastResponseId; snap = _messages.ToList(); includeReasoning = _activeIncludeReasoning; acceptsImages = _activeAcceptsImages; }
            var stripped = MessageAdapters.StripReasoning(snap, includeReasoning).ToList();
            foreach (var m in stripped) MessageAdapters.ValidateImages(m, acceptsImages);
            var (instructions, fullInput) = ResponsesTranslators.TranslateMessages(stripped);
            List<object> input;
            string? sendPrev = prev;
            int sent;
            lock (_lock) sent = _responsesSentCount;
            if (sendPrev is null) { input = fullInput; }
            else
            {
                int total = stripped.Count;
                int from = System.Math.Min(sent, total);
                var delta = stripped.Skip(from).ToList();
                var inc = new List<object>();
                foreach (var m in delta)
                {
                    if (m.Role == Role.User) { var (_, one) = ResponsesTranslators.TranslateMessages(new[] { m }); inc.AddRange(one); }
                    else if (m.Role == Role.Tool) { var (_, one) = ResponsesTranslators.TranslateMessages(new[] { m }); inc.AddRange(one); }
                }
                input = inc.Count > 0 ? inc : fullInput.TakeLast(1).ToList();
                if (instructions is not null && from == 0) { }
                else instructions = null;
            }
            var respTools = ResponsesTranslators.TranslateToolDefs(chatTools);
            ResponsesReply r;
            try
            {
                r = await _llm.ResponsesStreamAsync(sendPrev, instructions, input, respTools, evt =>
                {
                    switch (evt)
                    {
                        case StreamEvent.TextDelta td: _bus.PublishDelta(EventKinds.TextDelta, td.Text); break;
                        case StreamEvent.ReasoningDelta rd: _bus.PublishReasoningDelta(rd.Text, "summary"); break;
                        default: break;
                    }
                }, ct, _store.SessionId).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { _bus.FlushNow(); throw; }
            catch (EndpointStatusException ese) when (!rebuilt && ese.Status == 404 && (ese.Body.Contains("response_not_found") || ese.Body.Contains("expired") || ese.Body.Contains("invalid_previous_response")))
            {
                ClearResponseChain();
                rebuilt = true;
                continue;
            }
            catch (Exception ex)
            {
                var reason = RetryPolicy.GetRetryReason(ex);
                if (reason is null || attempt >= _retry) throw;
                attempt++;
                await HandleRetryAsync(attempt, reason, ct).ConfigureAwait(false);
                continue;
            }
            if (r.ResponseId is null)
            {
                SetResponseId(null);
                lock (_lock) _responsesSentCount = snap.Count;
                try { await _bus.PublishStateAsync(EventKinds.Notice, new NoticePayload { Text = "服务端未返回 response.id，已断链重建", Level = "info" }, ct).ConfigureAwait(false); } catch { }
            }
            else
            {
                SetResponseId(r.ResponseId);
                lock (_lock) _responsesSentCount = snap.Count + 1;
                try
                {
                    var hist = _store.LoadResponseId();
                    if (string.IsNullOrEmpty(hist)) { try { await _bus.PublishStateAsync(EventKinds.Notice, new NoticePayload { Text = "已重建上下文链", Level = "info" }, ct).ConfigureAwait(false); } catch { } }
                }
                catch { }
            }
            var reply = ToAssistantReply(r);
            if (RetryPolicy.IsEmptyReply(reply))
            {
                if (attempt >= _retry) { _bus.FlushNow(); return reply; }
                attempt++;
                await HandleRetryAsync(attempt, "空回复（模型没吐任何内容）", ct).ConfigureAwait(false);
                continue;
            }
            _bus.FlushNow();
            return reply;
        }
    }
    private static AssistantReply ToAssistantReply(ResponsesReply r)
    {
        var calls = new List<ToolCall>(r.ToolCalls);
        foreach (var u in r.UnknownOutputs)
        {
            var id = "call_" + Guid.NewGuid().ToString("N")[..8];
            var packed = ResponsesTranslators.PackUnknownArgs(u.RawType, u.RawJson.GetRawText());
            calls.Add(new ToolCall { Id = id, Type = "function", Function = new FunctionCall { Name = u.RawType, Arguments = packed } });
        }
        return new AssistantReply { Content = r.Content, ReasoningContent = r.ReasoningContent, ToolCalls = calls, Usage = r.Usage, TruncatedByLength = r.TruncatedByLength };
    }

    private async Task HandleRetryAsync(int attempt, string reason, CancellationToken ct)
    {
        // 通知 UI：把本轮已吐出的半截流式输出撤掉（否则会"半截 + 完整"拼接）
        await _bus.PublishStateAsync(EventKinds.LlmRetrying,
            new LlmRetryingPayload { Attempt = attempt, Max = _retry, Reason = reason },
            ct).ConfigureAwait(false);

        var delay = RetryPolicy.Backoff(attempt);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    // ============================================================
    //  工具执行（权限 + 分发）
    // ============================================================

    private async Task<string> ExecuteToolCallAsync(ToolCall tc, CancellationToken ct)
    {
        var callId = tc.Id;
        var name = tc.Function.Name;
        _currentToolCallId = callId;

        // ---------- I-4/I-5/I-6：参数解析 ----------
        JsonElement args;
        try
        {
            args = ToolArgumentParser.Parse(tc.Function.Arguments);
        }
        catch (Exception ex)
        {
            var msg = "工具参数解析失败: " + ex.Message + "\n"
                      + "请按工具定义的 JSON Schema 重新构造 arguments 后再次调用。";
            await EmitToolStartedAsync(callId, name, "", null, ct).ConfigureAwait(false);
            await EmitToolFinishedAsync(callId, name, false, msg, ct).ConfigureAwait(false);
            return msg;
        }

        var tool = _tools.Get(name);
        if (tool is null)
        {
            string friendly;
            if (name.StartsWith("mcp__", StringComparison.Ordinal))
            {
                friendly = "MCP 工具 `" + name + "` 在当前会话中尚未接入（MCP server 未连接或 mcp.json 未配置），无法执行。"
                           + "请检查数据目录下的 mcp.json 配置后重试，或改用内置工具完成任务。";
            }
            else if (string.Equals(name, "skill", StringComparison.Ordinal))
            {
                friendly = "skill 不是可直接调用的工具：请先用 read_file 查看对应技能目录下 SKILL.md 的正文，"
                           + "按里面的步骤用内置工具执行；不要再以 tool_call 形式调用 skill。";
            }
            else
            {
                var avail = string.Join(", ", _tools.Names);
                friendly = "未知工具: " + name + "。可用工具: " + avail;
            }
            await EmitToolStartedAsync(callId, name, ArgsPreview(args), null, ct).ConfigureAwait(false);
            await EmitToolFinishedAsync(callId, name, false, friendly, ct).ConfigureAwait(false);
            return friendly;
        }

        // ---------- 权限判定 ----------
        CommandRisk risk = CommandRisk.Safe;
        string command = "";

        if (tool.Operation == PermissionChecker.Operation.RunCommand)
        {
            command = args.TryGetProperty("command", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(command))
            {
                var msg = "command 不能为空: 请传入要执行的完整命令字符串，如 {\"command\": \"ls -la\"}";
                await EmitToolStartedAsync(callId, name, "", null, ct).ConfigureAwait(false);
                await EmitToolFinishedAsync(callId, name, false, msg, ct).ConfigureAwait(false);
                return msg;
            }

            risk = DangerousCommandInspector.Inspect(command, new InspectorOptions
            {
                Cwd = _cwd,
                Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                MaxDepth = 3,
            });
        }

        // 会话级"总是允许"快捷路径
        var alreadyAlways = tool.Operation switch
        {
            PermissionChecker.Operation.WriteFile => _fileAlways,
            PermissionChecker.Operation.RunCommand => _commandAlways && risk.Kind != CommandRiskKind.Blocked
                                                      && risk.Kind != CommandRiskKind.Uncertain,
            _ => false,
        };

        PermDecision decision;
        if (alreadyAlways)
        {
            decision = PermDecision.Allow;
        }
        else
        {
            decision = PermissionChecker.Check(_mode, tool.Operation, risk, _askInteractive);
        }

        if (decision.Kind == PermDecisionKind.Deny)
        {
            await EmitToolStartedAsync(callId, name, ArgsPreview(args), null, ct).ConfigureAwait(false);
            await EmitToolFinishedAsync(callId, name, false, decision.Reason ?? "已拒绝", ct)
                .ConfigureAwait(false);
            return "操作未执行: " + decision.Reason;
        }

        if (decision.Kind == PermDecisionKind.NeedAsk)
        {
            var answer = await AskPermissionAsync(new PermissionAsk
            {
                Kind = tool.Operation == PermissionChecker.Operation.WriteFile ? "write" : "command",
                Title = decision.Title ?? "确认",
                Body = tool.Operation == PermissionChecker.Operation.RunCommand ? command
                       : (decision.Body ?? ""),
                Warning = decision.Warning,
                AllowAlways = decision.AllowAlways,
            }, ct).ConfigureAwait(false);

            if (!answer.Allow)
            {
                await EmitToolStartedAsync(callId, name, ArgsPreview(args), null, ct).ConfigureAwait(false);
                await EmitToolFinishedAsync(callId, name, false, PermissionPolicies.UserDenied, ct)
                    .ConfigureAwait(false);
                return "操作未执行: " + PermissionPolicies.UserDenied;
            }

            if (answer.Always && decision.AllowAlways)
            {
                switch (tool.Operation)
                {
                    case PermissionChecker.Operation.WriteFile: _fileAlways = true; break;
                    case PermissionChecker.Operation.RunCommand: _commandAlways = true; break;
                    default: break;
                }
                await _bus.PublishStateAsync(EventKinds.Notice,
                    new NoticePayload { Text = "🔓 本会话都自动放行这类操作了", Level = "info" },
                    ct).ConfigureAwait(false);
            }
        }

        // ---------- 执行 ----------
        long? idleMs = null;
        if (name == "run_shell_command" &&
            args.TryGetProperty("idle_ms", out var idleEl) &&
            idleEl.ValueKind == JsonValueKind.Number)
        {
            idleMs = idleEl.GetInt64();
        }

        await EmitToolStartedAsync(callId, name, ArgsPreview(args), idleMs, ct).ConfigureAwait(false);

        try
        {
            var result = await tool.ExecuteAsync(args, new ToolContext
            {
                Cwd = _cwd,
                SessionId = _store.SessionId,
                Host = _self,
                Ct = ct,
                ToolCallId = callId,
            }, ct).ConfigureAwait(false);

            var output = TruncateForContext(result.Output);
            await EmitToolFinishedAsync(callId, name, result.Ok, output, ct).ConfigureAwait(false);
            return output;
        }
        catch (OperationCanceledException)
        {
            var msg = "⏹ 用户中断，该工具调用未执行。";
            await EmitToolFinishedAsync(callId, name, false, msg, CancellationToken.None)
                .ConfigureAwait(false);
            return msg;
        }
        catch (Exception ex)
        {
            var msg = "工具 " + name + " 执行失败: " + ex.Message;
            await EmitToolFinishedAsync(callId, name, false, msg, ct).ConfigureAwait(false);
            return msg;
        }
    }

    // ============================================================
    //  轮次续跑询问
    // ============================================================

    private async Task<bool> AskContinueRoundsAsync(int used, CancellationToken ct)
    {
        if (!_askInteractive) return false;

        var answer = await AskPermissionAsync(new PermissionAsk
        {
            Kind = "rounds",
            Title = "轮次预算用尽",
            Body = "已用 " + used + " 轮，任务可能还没完。",
            AllowAlways = false,
        }, ct).ConfigureAwait(false);

        return answer.Allow;
    }

    // ============================================================
    //  上下文压缩
    // ============================================================

    public async Task<(int removed, string summary)> CompactAsync(CancellationToken ct)
    {
        List<ChatMessage> snapshot;
        lock (_lock) snapshot = _messages.ToList();

        if (snapshot.Count <= KeepTailForCompact + 1) return (0, "");

        await _bus.PublishStateAsync(EventKinds.SessionCompactionStarted, null, ct).ConfigureAwait(false);

        var system = snapshot[0];
        var tail = snapshot.Skip(1).ToList();
        var splitAt = tail.Count - KeepTailForCompact;
        var old = tail.Take(splitAt).ToList();
        var keep = tail.Skip(splitAt).ToList();

        var body = string.Join("\n", old.Select(m =>
            "[" + RoleLabel(m.Role) + "] " + Truncate(MessageAdapters.StripImagesForSummary(m).Content ?? "", 2000)));


        var summaryMessages = new[]
        {
            ChatMessage.System(
                "你是会话上下文压缩器。把用户提供的对话压缩成精炼的中文要点列表，必须保留:\n" +
                "- 任务目标与当前进展到哪一步\n" +
                "- 已完成的关键操作与结果(重要的文件路径、命令、数据结论)\n" +
                "- 用户的明确决定、偏好与要求\n" +
                "- 尚未完成、需要继续处理的事项\n" +
                "只输出要点，不要复述对话原文，不要添加对话里没有的内容。"),
            ChatMessage.User(body),
        };

        string? summary;
        bool isResp;
        lock (_lock) isResp = string.Equals(_activeProtocol, "responses", StringComparison.OrdinalIgnoreCase);
        if (isResp)
        {
            var (instr, inp) = ResponsesTranslators.TranslateMessages(new[] { ChatMessage.System("你是会话上下文压缩器。把用户提供的对话压缩成精炼的中文要点列表，必须保留:\n- 任务目标与当前进展到哪一步\n- 已完成的关键操作与结果(重要的文件路径、命令、数据结论)\n- 用户的明确决定、偏好与要求\n- 尚未完成、需要继续处理的事项\n只输出要点，不要复述对话原文，不要添加对话里没有的内容。"), ChatMessage.User(body) });
            var rr = await _llm.ResponsesAsync(null, instr, inp, null, ct, _store.SessionId).ConfigureAwait(false);
            summary = rr.Content?.Trim();
        }
        else
        {
            var reply = await _llm.ChatAsync(summaryMessages, null, ct, _store.SessionId).ConfigureAwait(false);
            summary = reply.Content?.Trim();
        }
        if (string.IsNullOrEmpty(summary))
            throw new InvalidOperationException("压缩模型没返回内容，请再试一次");
        var newMessages = new List<ChatMessage>
        {
            system,
            ChatMessage.User(
                "【上下文摘要: 压缩自此前 " + old.Count + " 条消息，由模型自动生成，细节以历史文件为准】\n" + summary),
        };
        newMessages.AddRange(keep);

        // 成功才替换（失败保持原样）
        lock (_lock)
        {
            _messages.Clear();
            _messages.AddRange(newMessages);
        }
        ClearResponseChain();

        await _bus.PublishStateAsync(EventKinds.SessionCompacted,
            new SessionCompactedPayload
            {
                Removed = old.Count,
                Summary = summary,
                 // P08-fix 01：快照投影统一走 ProjectSnapshot（合并 tool 结果 + 稳定 Id/TsMs）。
                 Kept = MessageProjector.ProjectSnapshot(keep),
            }, ct).ConfigureAwait(false);

        return (old.Count, summary);
    }

    // ============================================================
    //  会话操作
    // ============================================================

    public void ClearContext()
    {
        lock (_lock)
        {
            _messages.Clear();
            PushSystemPromptLocked();
        }
        _store.Clear();
        ClearResponseChain();
        _ = _bus.PublishStateAsync(EventKinds.SessionContextCleared, null, CancellationToken.None);
    }

    public void StartNewSession()
    {
        lock (_lock)
        {
            _messages.Clear();
            _fileAlways = false;
            _commandAlways = false;
        }
        _store.StartNew();
        ClearResponseChain();
        lock (_lock) PushSystemPromptLocked();
        _ = PublishSessionInfoAsync(CancellationToken.None);
        _ = _bus.PublishStateAsync(EventKinds.SessionContextCleared, null, CancellationToken.None);
    }

     public (int count, int repaired) LoadHistory(string path)
     {
         var (meta, messages, repaired) = SessionStore.ReadHistory(path);
         _store.AttachTo(path);
         var metaCwd = meta?.Meta?.Cwd;
         bool restored = false;
         bool metaMissingDir = false;
         if (!string.IsNullOrWhiteSpace(metaCwd))
         {
             string full;
             try { full = Path.GetFullPath(metaCwd); } catch { full = ""; }
             if (!string.IsNullOrEmpty(full) && Directory.Exists(full))
             {
                 try { ThrowIfProtectedCwd(full); } catch { full = ""; }
                 if (!string.IsNullOrEmpty(full))
                 {
                     lock (_lock)
                     {
                         _cwd = full;
                         _fileAlways = false;
                         _commandAlways = false;
                     }
                     restored = true;
                 }
             }
             else
             {
                 metaMissingDir = true;
             }
         }
         lock (_lock)
         {
             _messages.Clear();
             PushSystemPromptLocked();
             _messages.AddRange(MessageAdapters.ToChatMessages(messages));
             if (restored) RefreshSystemPrompt();
         }
         ClearResponseChain();
         if (metaMissingDir)
         {
             _ = _bus.PublishStateAsync(EventKinds.Notice,
                 new NoticePayload { Text = "该会话记录的目录已不存在，保持当前目录", Level = "warn" },
                 CancellationToken.None);
         }
         _ = PublishSessionInfoAsync(CancellationToken.None);
         return (messages.Count, repaired);
     }

    // ============================================================
    //  快照
    // ============================================================

    public StateSnapshot BuildSnapshot()
    {
        lock (_lock)
        {
            var v = Environment.OSVersion.Version;
            return new StateSnapshot
            {
                Protocol = HaloVersion.ProtocolVersion,
                Engine = new EngineInfo
                {
                    Version = HaloVersion.Engine,
                    Pid = Environment.ProcessId,
                    StartedAtMs = 0,
                    OsVersion = v.ToString(),
                    IsWin7 = v.Major == 6 && v.Minor == 1,
                },
                Session = new SessionState
                {
                    Id = _store.SessionId,
                    Cwd = _cwd,
                    HistoryPath = _store.HistoryPath,
                },
                Agent = new AgentState
                {
                    Busy = _busy,
                    TurnId = _currentTurnId,
                    Mode = _mode,
                    ModeLabel = _mode.ToLabel(),
                    Persona = _persona,
                    Provider = _activeProviderId,
                    ProviderDisplay = _activeProviderDisplay,
                    Model = string.IsNullOrEmpty(_activeModelId) ? _llm.Model : _activeModelId,
                    ModelDisplay = _activeModelDisplay,
                    Protocol = _activeProtocol,
                    BaseUrl = _activeBaseUrl,
                    ApiKeySet = _activeApiKeySet,
                    ContextWindow = _activeContextWindow,
                    MaxTokens = _activeMaxTokens,
                    AcceptsImages = _activeAcceptsImages,
                    IncludeReasoning = _activeIncludeReasoning,
                    ProxyInUse = _activeProxyInUse,
                    MaxTurns = _maxTurns,
                    Retry = _retry,
                    Configured = _activeConfigured,
                    Round = new RoundState { Used = _roundUsed, Limit = _roundLimit },
                },

                Usage = new UsageState
                {
                    InputTotal = _usageIn,
                    OutputTotal = _usageOut,
                    LastPromptTokens = _lastPromptTokens,
                },
                 Queue = _inbox.ToArray(),
                 PendingAsk = _pendingAsk?.Ask,
                 // P08-fix 01：快照同源生成，统一走 ProjectSnapshot（合并 + 跳过 system/tool + 稳定 Id/TsMs）。
                 Messages = MessageProjector.ProjectSnapshot(_messages),
                 Notes = Array.Empty<Note>(),
                 Update = new UpdateState(),
             };
         }
     }

    // ============================================================
    //  IToolHost 实现
    // ============================================================

    public Task<UndoSnapshotResult> BackupAsync(string path, string action, CancellationToken ct)
    {
        try
        {
            var snap = UndoStore.Backup(_store.SessionId, path, action);
            return Task.FromResult(new UndoSnapshotResult(true, snap?.Snap, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new UndoSnapshotResult(false, null, ex.Message));
        }
    }

    public void PublishToolOutput(string toolCallId, string delta)
    {
        _bus.PublishDelta(EventKinds.ToolOutputDelta, delta, toolCallId);
    }

    public void PublishSilentAlert(string toolCallId, int level)
    {
        _ = _bus.PublishStateAsync(EventKinds.ToolSilentAlert,
            new ToolSilentAlertPayload { ToolCallId = toolCallId, Level = level },
            CancellationToken.None);
    }

    // ============================================================
    //  内部辅助
    // ============================================================

    private List<ChatMessage> MessagesSnapshot()
    {
        lock (_lock) return _messages.ToList();
    }

    private void Record(ChatMessage msg)
    {
        lock (_lock) _messages.Add(msg);
        _store.Record(MessageAdapters.ToStoredMessage(msg), headless: !_askInteractive, cwd: _cwd);
    }

    private void PushSystemPrompt()
    {
        lock (_lock) PushSystemPromptLocked();
    }

    private void PushSystemPromptLocked()
    {
        if (_messages.Count > 0 && _messages[0].Role == Role.System) return;

        var prompt = SystemPromptBuilder.Build(
            _cwd, _mode, _store.SessionId, _persona, _dataDir);
        _messages.Insert(0, ChatMessage.System(prompt));
    }

    private void RefreshSystemPrompt()
    {
        if (_messages.Count == 0 || _messages[0].Role != Role.System) return;

        _messages[0] = ChatMessage.System(SystemPromptBuilder.Build(
            _cwd, _mode, _store.SessionId, _persona, _dataDir));
    }

    private static ChatMessage ToChatMessage(AssistantReply reply, bool interrupted)
    {
        var content = reply.Content;
        if (interrupted)
            content = (content ?? "") + "\n[⏹ 生成被用户中断]";

        if (reply.ToolCalls.Count == 0)
            return ChatMessage.Assistant(content ?? "");

        return ChatMessage.AssistantWithToolCalls(
            string.IsNullOrEmpty(content) ? null : content,
            reply.ToolCalls);
    }

    private async Task EmitToolStartedAsync(
        string callId, string name, string args, long? idleMs, CancellationToken ct)
    {
        var (k, rt, _) = HaloAgent.Engine.Core.Llm.ResponsesTranslators.UnpackUnknownArgs(args);
        bool isUnknown = k == "unknown";
        await _bus.PublishStateAsync(EventKinds.ToolStarted,
            new ToolStartedPayload
            {
                ToolCallId = callId,
                Name = isUnknown ? (rt ?? name) : name,
                Args = args,
                IdleMs = idleMs,
                Kind = isUnknown ? "unknown" : "function",
                RawType = rt,
            }, ct).ConfigureAwait(false);
    }

    private async Task EmitToolFinishedAsync(
        string callId, string name, bool ok, string output, CancellationToken ct)
    {
        await _bus.PublishStateAsync(EventKinds.ToolFinished,
            new ToolFinishedPayload
            {
                ToolCallId = callId,
                Name = name,
                Ok = ok,
                Output = output,
            }, ct).ConfigureAwait(false);
    }

    private async Task FinishAsync(string turnId, string text, bool truncated, CancellationToken ct)
    {
        string? rid;
        lock (_lock) rid = _lastResponseId;
        await _bus.PublishStateAsync(EventKinds.TurnFinished,
            new TurnFinishedPayload
            {
                TurnId = turnId,
                Text = text,
                Truncated = truncated,
                ResponseId = rid,
            }, ct).ConfigureAwait(false);

        await PublishBusyAsync(false, ct).ConfigureAwait(false);
    }

    private async Task PublishBusyAsync(bool busy, CancellationToken ct)
    {
        int queueLen;
        lock (_lock) queueLen = _inbox.Count;

        await _bus.PublishStateAsync(EventKinds.StateBusyChanged,
            new BusyChangedPayload { Busy = busy, QueueLength = queueLen }, ct)
            .ConfigureAwait(false);
    }

    private async Task PublishSessionInfoAsync(CancellationToken ct)
    {
        await _bus.PublishStateAsync(EventKinds.SessionInfo,
            new SessionInfoPayload
            {
                Id = _store.SessionId,
                Cwd = _cwd,
                HistoryPath = _store.HistoryPath,
            }, ct).ConfigureAwait(false);
    }

    private static string ArgsPreview(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "";

    private static string RoleLabel(Role role) => role switch
    {
        Role.System => "系统",
        Role.User => "用户",
        Role.Assistant => "助手",
        Role.Tool => "工具结果",
        _ => "?",
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string TruncateForContext(string s) =>
        TextUtil.TruncateChars(s, ContextMaxChars, "\n…(结果过长已截断)");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record TurnResult(string Text, int ToolCalls, bool Truncated, long InputTokens, long OutputTokens);
