using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Methods;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Sessions;
using HaloAgent.Engine.Storage.Undo;
using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Server.Routing;

/// <summary>
/// 真实方法路由（P2 替换 P1 的 StubMethods）。
/// agent.prompt 的关键语义：立即返回 {accepted, turnId}，回合在后台跑，进度走事件流。
/// 这是 P1 契约的硬要求 —— 如果这里 await 回合，前端会卡在"发送中"直到整轮跑完（可能几分钟）。
/// </summary>
public sealed partial class RealMethods
{
    private readonly AgentSession _session;
    private readonly SessionStore _store;
    private readonly ToolRegistry _tools;
    private readonly string _dataDir;
    private readonly ILogger _log;
    private readonly CancellationToken _shutdownCt;

    public RealMethods(
        AgentSession session,
        SessionStore store,
        ToolRegistry tools,
        ILogger log,
        CancellationToken shutdownCt)
        : this(session, store, tools, Paths.DataDir, log, shutdownCt)
    {
    }

    public RealMethods(
        AgentSession session,
        SessionStore store,
        ToolRegistry tools,
        string dataDir,
        ILogger log,
        CancellationToken shutdownCt)
    {
        _session = session;
        _store = store;
        _tools = tools;
        _dataDir = dataDir;
        _log = log;
        _shutdownCt = shutdownCt;
    }

    public Task<object?> AgentPromptAsync(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var t) == true ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            throw RpcException.ParamsInvalid("text 不能为空");

        if (!_session.BuildSnapshot().Agent.Configured)
            throw new RpcException(RpcError.StateNotConfigured, "模型未配置：请去模型设置页选择 active provider/model 后再试。");

        if (_session.IsBusy)
            throw RpcException.Busy("有回合正在执行");

        var turnId = _session.NewTurnId();


        // 关键：不 await，只启动
        _ = Task.Run(async () =>
        {
            try
            {
                await _session.RunTurnAsync(text!, _shutdownCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常中断
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "回合执行异常");
            }
        }, CancellationToken.None);

        return Task.FromResult<object?>(new AgentPromptResult { Accepted = true, TurnId = turnId });
    }

    public Task<object?> AgentQueueAsync(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var t) == true ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            throw RpcException.ParamsInvalid("text 不能为空");

        var pos = _session.Enqueue(text!);
        return Task.FromResult<object?>(new AgentQueueResult { Queued = true, Position = pos });
    }

    public Task<object?> AgentUnqueueAsync(JsonElement? p, CancellationToken ct)
    {
        var text = p?.TryGetProperty("text", out var t) == true ? t.GetString() : "";
        return Task.FromResult<object?>(new AgentUnqueueResult
        {
            Removed = _session.TryDequeue(text ?? ""),
        });
    }

    public Task<object?> AgentCancelAsync(JsonElement? p, CancellationToken ct) =>
        Task.FromResult<object?>(new AgentCancelResult { Cancelled = _session.CancelTurn() });

    public Task<object?> AgentAnswerAsync(JsonElement? p, CancellationToken ct)
    {
        var askId = p?.TryGetProperty("askId", out var a) == true ? a.GetString() : null;
        var allow = p?.TryGetProperty("allow", out var al) == true && al.ValueKind == JsonValueKind.True;
        var always = p?.TryGetProperty("always", out var aw) == true && aw.ValueKind == JsonValueKind.True;

        if (string.IsNullOrEmpty(askId))
            throw RpcException.ParamsInvalid("askId 不能为空");

        var (accepted, reason) = _session.AnswerAsk(askId!, allow, always);
        return Task.FromResult<object?>(new AgentAnswerResult { Accepted = accepted, Reason = reason });
    }

    public Task<object?> AgentDrainAsync(JsonElement? p, CancellationToken ct) =>
        Task.FromResult<object?>(new AgentDrainResult { Text = _session.TryDrain() });

    public Task<object?> StateSnapshotAsync(JsonElement? p, CancellationToken ct) =>
        Task.FromResult<object?>(_session.BuildSnapshot());

    public async Task<object?> SessionCompactAsync(JsonElement? p, CancellationToken ct)
    {
        var (removed, summary) = await _session.CompactAsync(ct).ConfigureAwait(false);
        return new { removed, summary };
    }

    public Task<object?> SessionClearAsync(JsonElement? p, CancellationToken ct)
    {
        _session.ClearContext();
        return Task.FromResult<object?>(new OkResult { Ok = true });
    }

    public Task<object?> SessionNewAsync(JsonElement? p, CancellationToken ct)
    {
        // P08-fix 03：跑轮中切换会 corrupt 当前 turn，加 busy 守卫。
        if (_session.IsBusy)
            throw RpcException.Busy("有回合正在执行，暂不能新建会话");
        _session.StartNewSession();
        return Task.FromResult<object?>(new { id = _store.SessionId });
    }

    public Task<object?> SessionResumeAsync(JsonElement? p, CancellationToken ct)
    {
        // P08-fix 03：跑轮中切换会 corrupt 当前 turn，加 busy 守卫。
        if (_session.IsBusy)
            throw RpcException.Busy("有回合正在执行，暂不能切换会话");
        var id = p?.TryGetProperty("id", out var i) == true ? i.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw RpcException.ParamsInvalid("id 不能为空");

        // P08-fix 03/04：路径安全，堵 ../../ 逃逸。
        var safeId = SanitizeSessionId(id!);
        var path = Path.Combine(Paths.SessionsDir, safeId + ".jsonl");
        if (!File.Exists(path))
        {
            // P08-fix 03：合法空会话宽容（懒创建不变式：不预建空文件）。
            // 内存已丢（重启后）也覆盖：合法 s-* id 无文件即视为空会话，直接 attach + 清空。
            if (IsEmptySessionId(safeId))
            {
                var (emptyCount, _) = _session.LoadHistory(path);
                var emptySnap = _session.BuildSnapshot();
                return Task.FromResult<object?>(new
                {
                    count = emptyCount,
                    messages = emptySnap.Messages,
                });
            }
            throw new RpcException(RpcError.SessionNotFound, "会话不存在: " + safeId);
        }

        var (count, repaired) = _session.LoadHistory(path);
        if (repaired > 0)
        {
            _log.LogWarning("历史里有 {Count} 个孤儿 tool_calls，已补齐占位", repaired);
        }

        var snapshot = _session.BuildSnapshot();
        return Task.FromResult<object?>(new
        {
            count,
            messages = snapshot.Messages,
        });
    }

    /// <summary>P08-fix 03/04：会话 id 洗白，防路径穿越。含 / \ 或 GetFileName 后不一致即判非法。</summary>
    private static string SanitizeSessionId(string id)
    {
        var trimmed = id.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw RpcException.ParamsInvalid("id 不能为空");
        var clean = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(clean) || !string.Equals(clean, trimmed, StringComparison.Ordinal))
            throw RpcException.ParamsInvalid("id 不合法: " + id);
        if (clean.Contains("..", StringComparison.Ordinal))
            throw RpcException.ParamsInvalid("id 不合法: " + id);
        return clean;
    }

    /// <summary>P08-fix 03：合法空会话判定（s-* 形）。真不存在的 id 仍走 session.notFound。</summary>
    private static bool IsEmptySessionId(string safeId)
    {
        if (!safeId.StartsWith("s-", StringComparison.Ordinal) || safeId.Length > 80)
            return false;
        foreach (var c in safeId)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        }
        return true;
    }

    public Task<object?> SessionListAsync(JsonElement? p, CancellationToken ct)
    {
        var query = p?.TryGetProperty("query", out var q) == true ? q.GetString() : null;

        var items = SessionStore.ListHistory()
            .Where(h => string.IsNullOrWhiteSpace(query)
                        || h.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (h.Note?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || h.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(h => new SessionSummary
            {
                Id = h.Id,
                Title = h.Title,
                Note = h.Note,
                Cwd = h.Cwd,
                Headless = h.Headless,
                MtimeMs = h.MtimeMs,
                UserMsgs = h.UserMsgs,
                AssistantMsgs = h.AssistantMsgs,
            })
            .ToList();

        return Task.FromResult<object?>(new { items });
    }

    public Task<object?> SessionRenameAsync(JsonElement? p, CancellationToken ct)
    {
        var id = p?.TryGetProperty("id", out var i) == true ? i.GetString() : null;
        var note = p?.TryGetProperty("note", out var n) == true ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) throw RpcException.ParamsInvalid("id 不能为空");

        // P08-fix 04：防路径穿越。
        var safeId = SanitizeSessionId(id!);
        var path = Path.Combine(Paths.SessionsDir, safeId + ".jsonl");
        SessionStore.WriteNote(path, note);
        return Task.FromResult<object?>(new OkResult { Ok = true });
    }

    public Task<object?> SessionDeleteAsync(JsonElement? p, CancellationToken ct)
    {
        var id = p?.TryGetProperty("id", out var i) == true ? i.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) throw RpcException.ParamsInvalid("id 不能为空");

        // P08-fix 04：防路径穿越。
        var safeId = SanitizeSessionId(id!);
        var path = Path.Combine(Paths.SessionsDir, safeId + ".jsonl");
        SessionStore.DeleteHistory(path);
        return Task.FromResult<object?>(new OkResult { Ok = true });
    }

    public Task<object?> SessionSetCwdAsync(JsonElement? p, CancellationToken ct)
    {
        var path = p?.TryGetProperty("path", out var v) == true ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) throw RpcException.ParamsInvalid("path 不能为空");

        try { _session.SetCwd(path!); }
        catch (DirectoryNotFoundException ex)
        {
            throw new RpcException(RpcError.FsNotFound, ex.Message);
        }
        return Task.FromResult<object?>(new { cwd = _session.Cwd });
    }

    public Task<object?> StateSetModeAsync(JsonElement? p, CancellationToken ct)
    {
        var raw = p?.TryGetProperty("mode", out var m) == true ? m.GetString() : null;
        if (!PermissionModeExtensions.TryParseWire(raw, out var mode))
            throw RpcException.ParamsInvalid("mode 只能是 ask/acceptEdits/bypassPermissions/yolo（收到 " + raw + "）");

        _session.SetMode(mode);
        return Task.FromResult<object?>(new
        {
            mode = mode.ToWire(),
            label = mode.ToLabel(),
        });
    }

    public Task<object?> StateSetPersonaAsync(JsonElement? p, CancellationToken ct)
    {
        var name = p?.TryGetProperty("name", out var n) == true ? n.GetString() ?? "" : "";
        _session.SetPersona(name);
        return Task.FromResult<object?>(new { name });
    }

    public Task<object?> StateSetMaxTurnsAsync(JsonElement? p, CancellationToken ct)
    {
        var v = p?.TryGetProperty("value", out var el) == true && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : 200;
        _session.SetMaxTurns(v);
        return Task.FromResult<object?>(new { maxTurns = _session.MaxTurns });
    }

    public Task<object?> StateSetRetryAsync(JsonElement? p, CancellationToken ct)
    {
        var v = p?.TryGetProperty("value", out var el) == true && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : 0;
        _session.SetRetry(v);
        return Task.FromResult<object?>(new { retry = _session.Retry });
    }

    public Task<object?> ToolUndoListAsync(JsonElement? p, CancellationToken ct)
    {
        var items = UndoStore.List().Select((s, i) => new SnapshotInfo
        {
            Index = i + 1,
            Session = s.Session,
            Orig = s.Orig,
            Action = s.Action,
            TsMs = s.TsMs,
            External = s.IsExternal,
        }).ToList();

        return Task.FromResult<object?>(new { items });
    }

    public Task<object?> ToolUndoRollbackAsync(JsonElement? p, CancellationToken ct)
    {
        var index = p?.TryGetProperty("index", out var el) == true && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : 0;

        var snap = UndoStore.RollbackByIndex(index);
        return Task.FromResult<object?>(new { ok = true, orig = snap.Orig });
    }

    public Task<object?> ToolListAsync(JsonElement? p, CancellationToken ct)
    {
        var items = _tools.All.Select(t => new ToolInfo
        {
            Name = t.Name,
            Description = t.Description,
            Source = "builtin",
        }).ToList();

        return Task.FromResult<object?>(new { items });
    }
}
