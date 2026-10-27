using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Methods;
using HaloAgent.Engine.Abstractions.Types;

namespace HaloAgent.Engine.Server.Routing;

/// <summary>
/// P1 阶段的 stub 实现。
///
/// 为什么要有 stub：
///   - P1 的目标是"打通传输"，不是"实现功能"。
///   - 前端需要一套能跑通所有交互的"假引擎"来做 UI 开发。
///   - P2 逐个把这些 stub 替换为真实实现时，前端完全不用改——
///     因为契约（schema）没有变。
///
/// 注意：stub 必须返回**结构正确**的数据（类型、字段名都符合 schema），
///       否则前端的类型检查与渲染逻辑会被"假数据"误导。
/// </summary>
public sealed class StubMethods
{
    private readonly EngineState _state;

    public StubMethods(EngineState state) => _state = state;

    // ============================================================
    //  一次性注册：覆盖契约 54 个方法
    //  system 5 / agent 7 / session 8 / state 6 / settings 7
    //  / asset 12 / tool 4 / fs 3 / update 2
    // ============================================================

    public static void RegisterAll(MethodRouter router, StubMethods stub)
    {
        router.RegisterAll(
            // ---- system 5 ----
            ("system.ping", stub.SystemPing),
            ("system.info", stub.SystemInfo),
            ("system.protocol", stub.SystemProtocol),
            ("system.shutdown", stub.SystemShutdown),
            ("system.diagnostics", stub.EmptyOk),

            // ---- agent 7 ----
            ("agent.prompt", stub.AgentPrompt),
            ("agent.queue", stub.AgentQueue),
            ("agent.unqueue", stub.AgentUnqueue),
            ("agent.cancel", stub.AgentCancel),
            ("agent.answer", stub.AgentAnswer),
            ("agent.drain", stub.AgentDrain),
            ("agent.resync", stub.AgentResync),

            // ---- session 8 ----
            ("session.new", stub.SessionNew),
            ("session.resume", stub.NotImplemented),
            ("session.list", stub.EmptyList),
            ("session.rename", stub.EmptyOk),
            ("session.delete", stub.EmptyOk),
            ("session.clear", stub.EmptyOk),
            ("session.compact", stub.NotImplemented),
            ("session.setCwd", stub.NotImplemented),

            // ---- state 6 ----
            ("state.snapshot", stub.StateSnapshot),
            ("state.setMode", stub.StateSetMode),
            ("state.setPersona", stub.EmptyOk),
            ("state.setProvider", stub.EmptyOk),
            ("state.setMaxTurns", stub.EmptyOk),
            ("state.setRetry", stub.EmptyOk),

            // ---- settings 7 ----
            ("settings.get", stub.NotImplemented),
            ("settings.saveModel", stub.EmptyOk),
            ("settings.saveNetwork", stub.EmptyOk),
            ("settings.probeModels", stub.NotImplemented),
            ("settings.saveExtraHeaders", stub.EmptyOk),
            ("settings.mcp.get", stub.NotImplemented),
            ("settings.mcp.save", stub.EmptyOk),

            // ---- asset 12 ----
            ("asset.skills.list", stub.EmptyList),
            ("asset.skills.get", stub.NotImplemented),
            ("asset.skills.save", stub.EmptyOk),
            ("asset.skills.delete", stub.EmptyOk),
            ("asset.skills.run", stub.NotImplemented),
            ("asset.personas.list", stub.EmptyList),
            ("asset.personas.get", stub.NotImplemented),
            ("asset.personas.save", stub.EmptyOk),
            ("asset.memory.list", stub.EmptyList),
            ("asset.memory.get", stub.NotImplemented),
            ("asset.memory.save", stub.EmptyOk),
            ("asset.memory.delete", stub.EmptyOk),

            // ---- tool 4 ----
            ("tool.list", stub.EmptyList),
            ("tool.undoList", stub.EmptyList),
            ("tool.undoRollback", stub.NotImplemented),
            ("tool.mcpStatus", stub.NotImplemented),

            // ---- fs 3 ----
            ("fs.listDir", stub.EmptyList),
            ("fs.completePaths", stub.EmptyList),
            ("fs.validatePath", stub.NotImplemented),

            // ---- update 2 ----
            ("update.check", stub.NotImplemented),
            ("update.install", stub.NotImplemented));
    }

    // ============================================================
    //  system.*
    // ============================================================

    public Task<object?> SystemPing(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(new SystemPingResult
        {
            Pong = true,
            Version = Abstractions.HaloVersion.Engine,
            Protocol = Abstractions.HaloVersion.ProtocolVersion,
            Pid = Environment.ProcessId,
            UptimeMs = (long)(DateTime.UtcNow - _state.StartedAtUtc).TotalMilliseconds,
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

    public Task<object?> SystemInfo(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(new SystemInfoResult
        {
            Version = Abstractions.HaloVersion.Engine,
            Protocol = Abstractions.HaloVersion.ProtocolVersion,
            Pid = Environment.ProcessId,
            OsVersion = Environment.OSVersion.Version.ToString(),
            IsWin7 = IsWin7(),
            Is64BitOs = Environment.Is64BitOperatingSystem,
            Is64BitProcess = Environment.Is64BitProcess,
            Runtime = Environment.Version.ToString(),
            DataDir = _state.DataDir,
            StartedAtMs = new DateTimeOffset(_state.StartedAtUtc).ToUnixTimeMilliseconds(),
        });

    public Task<object?> SystemProtocol(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(new SystemProtocolResult
        {
            Version = Abstractions.HaloVersion.ProtocolVersion,
            Encoding = "ndjson",
            MaxFrameBytes = FrameCodec.DefaultMaxFrameBytes,
            Methods = _state.Router.Methods.OrderBy(x => x).ToArray(),
            Events = Abstractions.Events.EventKinds.All,
        });

    public Task<object?> SystemShutdown(JsonElement? _, CancellationToken ct)
    {
        _state.RequestShutdown();
        return Task.FromResult<object?>(new OkResult { Ok = true });
    }

    // ============================================================
    //  agent.*（P1 只返回 accepted，P2 真正跑回合）
    // ============================================================

    public Task<object?> AgentPrompt(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text))
            throw RpcException.ParamsInvalid("text 不能为空");

        if (_state.Busy)
            throw RpcException.Busy();

        var turnId = _state.BeginTurn(text);
        return Task.FromResult<object?>(new AgentPromptResult { Accepted = true, TurnId = turnId });
    }

    public Task<object?> AgentQueue(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text))
            throw RpcException.ParamsInvalid("text 不能为空");

        _state.Enqueue(text);
        return Task.FromResult<object?>(new AgentQueueResult
        {
            Queued = true,
            Position = _state.QueueLength,
        });
    }

    public Task<object?> AgentUnqueue(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";
        return Task.FromResult<object?>(new AgentUnqueueResult
        {
            Removed = _state.TryDequeue(text),
        });
    }

    public Task<object?> AgentCancel(JsonElement? p, CancellationToken ct)
    {
        var cancelled = _state.CancelTurn();
        return Task.FromResult<object?>(new AgentCancelResult { Cancelled = cancelled });
    }

    public Task<object?> AgentAnswer(JsonElement? p, CancellationToken ct)
    {
        var askId = p?.TryGetProperty("askId", out var a) == true ? a.GetString() ?? "" : "";
        var allow = p?.TryGetProperty("allow", out var al) == true && al.GetBoolean();
        var always = p?.TryGetProperty("always", out var aw) == true && aw.GetBoolean();

        var result = _state.AnswerAsk(askId, allow, always);
        return Task.FromResult<object?>(result);
    }

    public Task<object?> AgentDrain(JsonElement? _, CancellationToken ct)
    {
        var text = _state.TryDrain();
        return Task.FromResult<object?>(new AgentDrainResult { Text = text });
    }

    public Task<object?> AgentResync(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(_state.BuildSnapshot());

    // ============================================================
    //  session.*（P1 最小 stub：仅 new 返回可用形状）
    // ============================================================

    public Task<object?> SessionNew(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(new { id = _state.SessionId });

    // ============================================================
    //  state.*
    // ============================================================

    public Task<object?> StateSnapshot(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(_state.BuildSnapshot());

    public Task<object?> StateSetMode(JsonElement? p, CancellationToken ct)
    {
        var raw = p?.TryGetProperty("mode", out var m) == true ? m.GetString() : null;
        if (!PermissionModeExtensions.TryParseWire(raw, out var mode))
            throw RpcException.ParamsInvalid($"mode 只能是 ask/acceptEdits/bypassPermissions/yolo（收到 {raw}）");

        _state.Mode = mode;
        return Task.FromResult<object?>(new
        {
            mode = mode.ToWire(),
            label = mode.ToLabel(),
        });
    }

    // ============================================================
    //  其余方法（P1 返回最小可用 stub）
    // ============================================================

    public Task<object?> EmptyOk(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(new OkResult { Ok = true });

    public Task<object?> EmptyList(JsonElement? _, CancellationToken ct) =>
        Task.FromResult<object?>(new { items = Array.Empty<object>() });

    public Task<object?> NotImplemented(JsonElement? _, CancellationToken ct) =>
        throw new RpcException(RpcError.NotSupported,
            "该功能在 P1 阶段未实现（stub）", "P2 阶段会实现");

    private static bool IsWin7()
    {
        var v = Environment.OSVersion.Version;
        return v.Major == 6 && v.Minor == 1;
    }
}
