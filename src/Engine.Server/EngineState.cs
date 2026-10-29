using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Server;

/// <summary>
/// 引擎运行状态（P1 简版）。
///
/// P2 会把这一份状态搬进 Engine.Core 的 AgentSession，
/// 但协议面的形状不变 —— 这就是"先定契约、后填实现"的好处。
/// P10：stub/未配置态同样使用新 AgentState 形状。
/// </summary>
public sealed class EngineState
{
    private readonly object _lock = new();
    private readonly List<string> _queue = new();
    private readonly List<MessageView> _messages = new();

    public EngineState(string dataDir, Routing.MethodRouter router)
    {
        DataDir = dataDir;
        Router = router;
        StartedAtUtc = DateTime.UtcNow;
    }

    public DateTime StartedAtUtc { get; }
    public string DataDir { get; }
    public Routing.MethodRouter Router { get; }

    public string SessionId { get; private set; } = "s-" + Guid.NewGuid().ToString("N")[..12];
    public string Cwd { get; set; } = Environment.CurrentDirectory;
    public string? HistoryPath { get; private set; }

    public PermissionMode Mode { get; set; } = PermissionMode.Ask;
    public string Persona { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderDisplay { get; set; } = "";
    public string Model { get; set; } = "";
    public string ModelDisplay { get; set; } = "";
    public string Protocol { get; set; } = "chat";
    public string BaseUrl { get; set; } = "";
    public bool ApiKeySet { get; set; }
    public int? ContextWindow { get; set; }
    public int? MaxTokens { get; set; }
    public bool AcceptsImages { get; set; }
    public bool IncludeReasoning { get; set; } = true;
    public bool ProxyInUse { get; set; }
    public bool Configured { get; set; }
    public int MaxTurns { get; set; } = 200;
    public int Retry { get; set; }

    public bool Busy { get; private set; }
    public string? TurnId { get; private set; }
    public int RoundUsed { get; private set; }
    public int RoundLimit { get; set; } = 200;

    public long UsageInput { get; private set; }
    public long UsageOutput { get; private set; }
    public long? LastPromptTokens { get; private set; }

    public PendingAsk? PendingAsk { get; private set; }
    private int _askSeq;

    private bool _shutdownRequested;
    public bool ShutdownRequested => _shutdownRequested;
    public void RequestShutdown() => _shutdownRequested = true;

    public int QueueLength { get { lock (_lock) return _queue.Count; } }

    // ---------------- 回合 ----------------

    public string BeginTurn(string text)
    {
        lock (_lock)
        {
            if (Busy) throw new InvalidOperationException("已有回合在执行");
            Busy = true;
            TurnId = "t-" + Guid.NewGuid().ToString("N")[..8];
            RoundUsed = 0;
            _messages.Add(new MessageView
            {
                Id = "m-" + Guid.NewGuid().ToString("N")[..8],
                Role = "user",
                Text = text,
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            return TurnId;
        }
    }

    public void EndTurn()
    {
        lock (_lock)
        {
            Busy = false;
            TurnId = null;
        }
    }

    public bool CancelTurn()
    {
        lock (_lock)
        {
            if (!Busy) return false;
            Busy = false;
            TurnId = null;
            return true;
        }
    }

    // ---------------- 队列 ----------------

    public void Enqueue(string text) { lock (_lock) _queue.Add(text); }

    public bool TryDequeue(string text)
    {
        lock (_lock)
        {
            var idx = _queue.IndexOf(text);
            if (idx < 0) return false;
            _queue.RemoveAt(idx);
            return true;
        }
    }

    public string? TryDrain()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return null;
            var t = _queue[0];
            _queue.RemoveAt(0);
            return t;
        }
    }

    // ---------------- 询问 ----------------

    public PendingAsk CreatePermissionAsk(string kind, string title, string body, string? warning)
    {
        lock (_lock)
        {
            var ask = PendingAsk.Permission(
                askId: $"q{++_askSeq}",
                kind: kind,
                title: title,
                body: body,
                warning: warning,
                allowAlways: warning is null);
            PendingAsk = ask;
            return ask;
        }
    }

    public PendingAsk CreateRoundsAsk(int used)
    {
        lock (_lock)
        {
            var ask = PendingAsk.Rounds($"q{++_askSeq}", used);
            PendingAsk = ask;
            return ask;
        }
    }

    public Abstractions.Methods.AgentAnswerResult AnswerAsk(string askId, bool allow, bool always)
    {
        lock (_lock)
        {
            if (PendingAsk is null)
                return new Abstractions.Methods.AgentAnswerResult { Accepted = false, Reason = "no-pending" };
            if (PendingAsk.AskId != askId)
                return new Abstractions.Methods.AgentAnswerResult { Accepted = false, Reason = "stale" };

            // 安全政策：warning 存在时忽略 always
            var effectiveAlways = always && PendingAsk.Warning is null;
            _ = effectiveAlways;
            PendingAsk = null;
            return new Abstractions.Methods.AgentAnswerResult { Accepted = true, Reason = null };
        }
    }

    // ---------------- 快照 ----------------

    public StateSnapshot BuildSnapshot()
    {
        lock (_lock)
        {
            var v = Environment.OSVersion.Version;
            return new StateSnapshot
            {
                Protocol = Abstractions.HaloVersion.ProtocolVersion,
                Engine = new EngineInfo
                {
                    Version = Abstractions.HaloVersion.Engine,
                    Pid = Environment.ProcessId,
                    StartedAtMs = new DateTimeOffset(StartedAtUtc).ToUnixTimeMilliseconds(),
                    OsVersion = v.ToString(),
                    IsWin7 = v.Major == 6 && v.Minor == 1,
                },
                Session = new SessionState
                {
                    Id = SessionId,
                    Cwd = Cwd,
                    HistoryPath = HistoryPath,
                },
                Agent = new AgentState
                {
                    Busy = Busy,
                    TurnId = TurnId,
                    Mode = Mode,
                    ModeLabel = Mode.ToLabel(),
                    Persona = Persona,
                    Provider = Provider,
                    ProviderDisplay = ProviderDisplay,
                    Model = Model,
                    ModelDisplay = ModelDisplay,
                    Protocol = Protocol,
                    BaseUrl = BaseUrl,
                    ApiKeySet = ApiKeySet,
                    ContextWindow = ContextWindow,
                    MaxTokens = MaxTokens,
                    AcceptsImages = AcceptsImages,
                    IncludeReasoning = IncludeReasoning,
                    ProxyInUse = ProxyInUse,
                    MaxTurns = MaxTurns,
                    Retry = Retry,
                    Configured = Configured,
                    Round = new RoundState { Used = RoundUsed, Limit = RoundLimit },
                },
                Usage = new UsageState
                {
                    InputTotal = UsageInput,
                    OutputTotal = UsageOutput,
                    LastPromptTokens = LastPromptTokens,
                },
                Queue = _queue.ToArray(),
                PendingAsk = PendingAsk,
                Messages = _messages.ToArray(),
                Notes = Array.Empty<Note>(),
                Update = new UpdateState(),
            };
        }
    }
}
