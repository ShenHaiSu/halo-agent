using System.Collections.Concurrent;
using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>need09-06：ask_user 问卷模型。</summary>
public sealed class AskUserOption
{
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class AskUserQuestion
{
    public string Id { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public List<AskUserOption> Options { get; set; } = new();
    public bool MultiSelect { get; set; }
    public bool AllowCustom { get; set; }
    public string? Default { get; set; }
}

public sealed class AskUserOutcome
{
    public bool Resolved { get; set; }
    public string AnswersJson { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>
/// need09-06：ask_user 阻塞门（sessionId → 单 pending）。
/// 第二并发直接 Fail；超时/取消转 Fail 不挂起；ticket 对不上报 ASK_TICKET_MISMATCH。
/// </summary>
public static class AskUserGate
{
    /// <summary>默认等待 300s；单测可覆盖（如 1s）。</summary>
    public static int DefaultTimeoutSec = 300;

    internal sealed class Pending
    {
        public string TicketId { get; init; } = string.Empty;
        public string CallId { get; init; } = string.Empty;
        public List<AskUserQuestion> Questions { get; init; } = new();
        public TaskCompletionSource<AskUserOutcome> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<string, Pending> _pendings =
        new(StringComparer.Ordinal);

    public static bool HasPending(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _pendings.ContainsKey(sessionId.Trim());

    public static void ClearAll() => _pendings.Clear();

    /// <summary>查询当前待回答 ticket（诊断/单测用；无 pending 返回 null）。</summary>
    public static string? TryGetTicket(string sessionId) =>
        _pendings.TryGetValue((sessionId ?? string.Empty).Trim(), out var p) ? p.TicketId : null;

    /// <summary>尝试创建待回答；已有未完成 pending 则返回 Created=false（第二个并发 Fail）。</summary>
    public static (bool Created, string TicketId, string? ConflictTicket) TryCreate(
        string sessionId,
        string callId,
        List<AskUserQuestion> questions
    )
    {
        string sid = (sessionId ?? string.Empty).Trim();
        string ticket = "ask_" + Guid.NewGuid().ToString("N")[..16];
        var pending = new Pending
        {
            TicketId = ticket,
            CallId = callId ?? string.Empty,
            Questions = questions,
        };

        while (true)
        {
            if (_pendings.TryAdd(sid, pending))
                return (true, ticket, null);
            if (_pendings.TryGetValue(sid, out var existing))
            {
                if (existing.Tcs.Task.IsCompleted)
                {
                    if (_pendings.TryRemove(new KeyValuePair<string, Pending>(sid, existing)))
                        continue;
                    return (false, string.Empty, existing.TicketId);
                }
                return (false, string.Empty, existing.TicketId);
            }
        }
    }

    /// <summary>前端提交答案（ticket 必须匹配，否则 ASK_TICKET_MISMATCH）。</summary>
    public static (bool Ok, string Code, string Message) TryResolve(
        string sessionId,
        string ticketId,
        string answersJson
    )
    {
        string sid = (sessionId ?? string.Empty).Trim();
        if (!_pendings.TryGetValue(sid, out var pending))
            return (false, "ASK_TICKET_MISMATCH", "当前会话没有待回答的提问（可能已超时或取消），ticket 无法匹配。");
        if (
            !string.Equals(
                pending.TicketId,
                (ticketId ?? string.Empty).Trim(),
                StringComparison.Ordinal
            )
        )
            return (
                false,
                "ASK_TICKET_MISMATCH",
                $"ticket 不匹配：期待 {pending.TicketId}，收到 {(ticketId ?? string.Empty).Trim()}。"
            );
        _pendings.TryRemove(new KeyValuePair<string, Pending>(sid, pending));
        pending.Tcs.TrySetResult(
            new AskUserOutcome
            {
                Resolved = true,
                AnswersJson = answersJson ?? string.Empty,
                Outcome = "submitted",
            }
        );
        try
        {
            JsEventDispatcher.Publish(
                "agent:ask-user-resolved",
                new
                {
                    sessionId = sid,
                    ticketId = pending.TicketId,
                    outcome = "submitted"
                }
            );
        }
        catch
        { /* 事件失败不影响结果 */
        }
        return (true, string.Empty, "已提交回答");
    }

    internal static Pending? GetPending(string sessionId) =>
        _pendings.TryGetValue((sessionId ?? string.Empty).Trim(), out var p) ? p : null;

    internal static void RemoveTimeout(string sessionId, string ticketId, string outcome)
    {
        string sid = (sessionId ?? string.Empty).Trim();
        if (
            _pendings.TryGetValue(sid, out var pending)
            && string.Equals(pending.TicketId, ticketId, StringComparison.Ordinal)
        )
        {
            _pendings.TryRemove(new KeyValuePair<string, Pending>(sid, pending));
            pending.Tcs.TrySetResult(new AskUserOutcome { Resolved = false, Outcome = outcome });
            try
            {
                JsEventDispatcher.Publish(
                    "agent:ask-user-resolved",
                    new
                    {
                        sessionId = sid,
                        ticketId,
                        outcome
                    }
                );
                HaloLogger.App.Warn($"ask_user 等待结束：session={sid} outcome={outcome}");
            }
            catch
            { /* ignore */
            }
        }
    }

    internal static void PublishRequested(
        string sessionId,
        string ticketId,
        string callId,
        List<AskUserQuestion> questions
    )
    {
        try
        {
            JsEventDispatcher.Publish(
                "agent:ask-user-requested",
                new
                {
                    sessionId,
                    ticketId,
                    callId,
                    questions = questions
                        .Select(q => new
                        {
                            id = q.Id,
                            question = q.Question,
                            options = q
                                .Options.Select(o => new
                                {
                                    label = o.Label,
                                    description = o.Description
                                })
                                .ToList(),
                            multiSelect = q.MultiSelect,
                            allowCustom = q.AllowCustom,
                        })
                        .ToList(),
                }
            );
        }
        catch
        { /* ignore */
        }
    }
}
