using HaloAgent.Engine.Core.Llm;

namespace HaloAgent.Engine.Core.Tests.TestSupport;

/// <summary>一次脚本化回复的配置。</summary>
public sealed class ScriptedReply
{
    public string? Content { get; set; }
    public string? Reasoning { get; set; }
    public List<ToolCall> ToolCalls { get; } = new();
    public Usage Usage { get; set; } = new(0, 0);

    /// <summary>流式分片粒度：每 N 个字符一块。设为 0 表示一次性给完。</summary>
    public int ChunkSize { get; set; } = 3;

    /// <summary>模拟异常（429 / 断连 / 空回复）。</summary>
    public Exception? Throw { get; set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>便捷：追加一次工具调用。</summary>
    public void AddCall(string id, string name, string arguments)
    {
        ToolCalls.Add(new ToolCall
        {
            Id = id,
            Function = new FunctionCall { Name = name, Arguments = arguments },
        });
    }
}

/// <summary>每次 LLM 调用的入参快照（深拷贝，断言“机制真的生效”的唯一证据）。</summary>
public sealed record LlmCallSnapshot(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<object>? Tools,
    string? SessionId);

/// <summary>
/// 脚本化假 LLM 客户端。
///
/// 设计要点：
///  1. 用 Queue 按顺序消费；用完了再调就抛异常（暴露“多调了一次”）。
///  2. 记录每次调用的完整入参快照。
///  3. 流式时按 ChunkSize 切片，模拟真实的 SSE 多帧行为。
///  4. Throw 用来测重试；Delay（ct 可中断）用来测中断。
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<ScriptedReply> _script = new();
    private readonly object _gate = new();

    /// <summary>每次调用的入参快照。</summary>
    public List<LlmCallSnapshot> Calls { get; } = new();

    public string Model => "fake-model";

    public string? ProxyUrl => null;

    public int PendingScripts
    {
        get { lock (_gate) return _script.Count; }
    }

    public void Enqueue(Action<ScriptedReply> configure)
    {
        var r = new ScriptedReply();
        configure(r);
        lock (_gate) _script.Enqueue(r);
    }

    public void EnqueueContent(string content) => Enqueue(r => r.Content = content);

    public void EnqueueThrow(Exception ex) => Enqueue(r => r.Throw = ex);

    public Task<AssistantReply> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<object>? tools,
        CancellationToken ct,
        string? sessionId = null)
    {
        // 压缩路径用：无流式事件，但同样记录快照、支持 Delay/Throw。
        var reply = Next(messages, tools, sessionId);
        if (reply.Delay > TimeSpan.Zero)
        {
            // 同步等待会阻塞调用线程；压缩测试里 Delay 恒为零，此处用 GetAwaiter 保持同步签名语义。
            Task.Delay(reply.Delay, ct).GetAwaiter().GetResult();
        }

        ct.ThrowIfCancellationRequested();
        if (reply.Throw is not null) throw reply.Throw;
        return Task.FromResult(ToReply(reply));
    }

    public async Task<AssistantReply> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<object>? tools,
        Action<StreamEvent> onEvent,
        CancellationToken ct,
        string? sessionId = null)
    {
        var reply = Next(messages, tools, sessionId);

        if (reply.Delay > TimeSpan.Zero)
            await Task.Delay(reply.Delay, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        if (reply.Throw is not null) throw reply.Throw;

        if (!string.IsNullOrEmpty(reply.Reasoning))
        {
            foreach (var piece in Slice(reply.Reasoning, reply.ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                onEvent(new StreamEvent.ReasoningDelta(piece));
            }
        }

        if (!string.IsNullOrEmpty(reply.Content))
        {
            foreach (var piece in Slice(reply.Content, reply.ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                onEvent(new StreamEvent.TextDelta(piece));
            }
        }

        return ToReply(reply);
    }

    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    public void Reconfigure(LlmConfig cfg)
    {
        // 假客户端忽略重配置。
    }
    public object BuildResponsesBody(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, bool stream)
    {
        var d = new Dictionary<string, object?>();
        d["model"] = Model;
        if (previousResponseId is not null) d["previous_response_id"] = previousResponseId;
        if (instructions is not null) d["instructions"] = instructions;
        d["input"] = input;
        if (tools is { Count: > 0 }) d["tools"] = tools;
        d["stream"] = stream;
        d["store"] = false;
        return d;
    }
    public Task<ResponsesReply> ResponsesAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, CancellationToken ct, string? sessionId = null)
    {
        var reply = Next(Array.Empty<ChatMessage>(), null, sessionId);
        if (reply.Throw is not null) throw reply.Throw;
        return Task.FromResult(new ResponsesReply { ResponseId = "resp-fake", Content = reply.Content, ReasoningContent = reply.Reasoning, ReasoningKind = "summary", ToolCalls = reply.ToolCalls.ToList(), Usage = reply.Usage });
    }
    public async Task<ResponsesReply> ResponsesStreamAsync(string? previousResponseId, string? instructions, IReadOnlyList<object> input, IReadOnlyList<ResponsesToolDef>? tools, Action<StreamEvent> onEvent, CancellationToken ct, string? sessionId = null)
    {
        var reply = Next(Array.Empty<ChatMessage>(), null, sessionId);
        if (reply.Throw is not null) throw reply.Throw;
        if (!string.IsNullOrEmpty(reply.Reasoning)) foreach (var p in Slice(reply.Reasoning, reply.ChunkSize)) onEvent(new StreamEvent.ReasoningDelta(p));
        if (!string.IsNullOrEmpty(reply.Content)) foreach (var p in Slice(reply.Content, reply.ChunkSize)) onEvent(new StreamEvent.TextDelta(p));
        await Task.Yield();
        return new ResponsesReply { ResponseId = "resp-fake", Content = reply.Content, ReasoningContent = reply.Reasoning, ReasoningKind = "summary", ToolCalls = reply.ToolCalls.ToList(), Usage = reply.Usage };
    }

    private ScriptedReply Next(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<object>? tools,
        string? sessionId)
    {
        lock (_gate)
        {
            if (_script.Count == 0)
            {
                throw new InvalidOperationException(
                    "FakeLlmClient 脚本耗尽：模型调用次数超出 Enqueue 的脚本数（多调了一次？循环没收敛？）。");
            }

            var reply = _script.Dequeue();
            Calls.Add(new LlmCallSnapshot(
                messages.Select(m => m with
                {
                    ToolCalls = m.ToolCalls is null ? null : new List<ToolCall>(m.ToolCalls),
                }).ToList(),
                tools is null ? null : tools.ToList(),
                sessionId));
            return reply;
        }
    }

    private static AssistantReply ToReply(ScriptedReply reply) => new()
    {
        Content = reply.Content,
        ReasoningContent = reply.Reasoning,
        ToolCalls = reply.ToolCalls.ToList(),
        Usage = reply.Usage,
    };

    private static IEnumerable<string> Slice(string text, int chunkSize)
    {
        if (chunkSize <= 0 || text.Length == 0)
        {
            yield return text;
            yield break;
        }

        for (int i = 0; i < text.Length; i += chunkSize)
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
    }
}
