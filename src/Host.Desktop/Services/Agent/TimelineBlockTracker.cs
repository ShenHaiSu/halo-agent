using Host.Desktop.Core.Models;
using Host.Desktop.LlmGateway.Models;

namespace Host.Desktop.Services;

/// <summary>
/// Streaming block tracker: one contiguous run of the same kind forms one draft.
/// Sequence and epoch come from the gateway and are only passed through.
/// Empty text and fragmented reasoning are merged to avoid block explosion.
/// </summary>
public sealed class TimelineBlockTracker
{
    private readonly string _requestId;
    private readonly int _iteration;
    private int _blockIndex;
    private TimelineBlockDraft? _current;

    public List<TimelineBlockDraft> Drafts { get; } = new();

    public TimelineBlockTracker(string requestId, int iteration)
    {
        _requestId = requestId ?? string.Empty;
        _iteration = iteration;
    }

    private TimelineBlockDraft Open(
        string kind,
        UnifiedStreamEvent evt,
        string? callId,
        string? functionName
    )
    {
        _blockIndex++;
        var draft = new TimelineBlockDraft
        {
            BlockId =
                _requestId
                + ":b"
                + _blockIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sequence = evt.Sequence,
            Epoch = evt.Epoch ?? string.Empty,
            Kind = kind,
            Tag = string.Equals(kind, TimelineBlockKind.Text, StringComparison.Ordinal)
                ? TimelineBlockTag.Pending
                : TimelineBlockTag.Process,
            CallId = callId,
            FunctionName = functionName,
            Iteration = _iteration,
        };
        Drafts.Add(draft);
        _current = draft;
        return draft;
    }

    private TimelineBlockDraft Ensure(
        string kind,
        UnifiedStreamEvent evt,
        string? callId = null,
        string? functionName = null
    )
    {
        if (
            _current != null
            && string.Equals(_current.Kind, kind, StringComparison.Ordinal)
            && string.Equals(
                _current.CallId ?? string.Empty,
                callId ?? string.Empty,
                StringComparison.Ordinal
            )
        )
        {
            return _current;
        }
        return Open(kind, evt, callId, functionName);
    }

    public void RecordReasoning(UnifiedStreamEvent evt)
    {
        string delta = evt.TextDelta ?? string.Empty;
        if (delta.Length == 0)
        {
            return;
        }
        var draft = Ensure(TimelineBlockKind.Reasoning, evt);
        draft.Text.Append(delta);
    }

    public void RecordText(UnifiedStreamEvent evt)
    {
        string delta = evt.TextDelta ?? string.Empty;
        if (delta.Length == 0)
        {
            return;
        }
        if (
            string.IsNullOrWhiteSpace(delta)
            && (
                _current == null
                || !string.Equals(_current.Kind, TimelineBlockKind.Text, StringComparison.Ordinal)
                || _current.Text.Length == 0
            )
        )
        {
            return;
        }
        var draft = Ensure(TimelineBlockKind.Text, evt);
        draft.Text.Append(delta);
    }

    public void RecordToolStarted(UnifiedStreamEvent evt)
    {
        string callId = evt.ToolCall?.CallId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }
        string name = evt.ToolCall?.FunctionName ?? string.Empty;
        var draft = Open(TimelineBlockKind.ToolCall, evt, callId, name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            draft.FunctionName = name;
        }
    }

    public void RecordToolArgs(UnifiedStreamEvent evt)
    {
        string callId = evt.ToolCall?.CallId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }
        string delta = evt.TextDelta ?? string.Empty;
        TimelineBlockDraft? draft = null;
        for (int i = Drafts.Count - 1; i >= 0; i--)
        {
            var cand = Drafts[i];
            if (
                string.Equals(cand.Kind, TimelineBlockKind.ToolCall, StringComparison.Ordinal)
                && string.Equals(cand.CallId, callId, StringComparison.Ordinal)
            )
            {
                draft = cand;
                break;
            }
        }
        if (draft == null)
        {
            draft = Open(TimelineBlockKind.ToolCall, evt, callId, evt.ToolCall?.FunctionName);
        }
        _current = draft;
        if (!string.IsNullOrWhiteSpace(evt.ToolCall?.FunctionName))
        {
            draft.FunctionName = evt.ToolCall!.FunctionName;
        }
        if (delta.Length > 0)
        {
            draft.Args.Append(delta);
        }
    }

    public void RecordToolDone(UnifiedStreamEvent evt)
    {
        string callId = evt.ToolCall?.CallId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }
        TimelineBlockDraft? draft = null;
        for (int i = Drafts.Count - 1; i >= 0; i--)
        {
            var cand = Drafts[i];
            if (
                string.Equals(cand.Kind, TimelineBlockKind.ToolCall, StringComparison.Ordinal)
                && string.Equals(cand.CallId, callId, StringComparison.Ordinal)
            )
            {
                draft = cand;
                break;
            }
        }
        if (draft == null)
        {
            draft = Open(TimelineBlockKind.ToolCall, evt, callId, evt.ToolCall?.FunctionName);
        }
        _current = draft;
        if (!string.IsNullOrWhiteSpace(evt.ToolCall?.FunctionName))
        {
            draft.FunctionName = evt.ToolCall!.FunctionName;
        }
        string fullArgs = evt.ToolCall?.ArgumentsJson ?? string.Empty;
        if (fullArgs.Length > 0)
        {
            draft.Args.Clear();
            draft.Args.Append(fullArgs);
        }
    }

    public void CloseCurrent()
    {
        _current = null;
    }

    public static string BuildPreview(string text, int maxGraphemes)
    {
        if (string.IsNullOrEmpty(text) || maxGraphemes <= 0)
        {
            return string.Empty;
        }
        try
        {
            var elements = System.Globalization.StringInfo.ParseCombiningCharacters(text);
            if (elements.Length <= maxGraphemes)
            {
                return text;
            }
            int cut = elements[maxGraphemes];
            return text.Substring(0, cut);
        }
        catch
        {
            return text.Length <= maxGraphemes ? text : text.Substring(0, maxGraphemes);
        }
    }
}
