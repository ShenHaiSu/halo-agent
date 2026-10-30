namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>
/// 渲染友好的一条消息。
/// 与 OpenAI 的 ChatMessage 的区别：OpenAI 格式把 tool 结果做成独立的 tool 消息
/// （用 tool_call_id 关联），前端要自己配对；这里把“工具调用 + 它的结果”合并进
/// assistant 消息的 toolCalls 数组，一项即一张工具卡片，前端直接渲染。
/// </summary>
public sealed record MessageView
{
    /// <summary>消息 id。</summary>
    public string Id { get; init; } = "";
    /// <summary>“user” | “assistant” | “tool” | “system”。</summary>
    public string Role { get; init; } = "";
    /// <summary>正文。</summary>
    public string Text { get; init; } = "";
    /// <summary>思考过程（可为空）。</summary>
    public string? Reasoning { get; init; }
    /// <summary>推理种类：full(chat) | summary(responses)。缺省回落 full。</summary>
    public string? ReasoningKind { get; init; }
    /// <summary>诊断用 response.id，不做链逻辑依赖。</summary>
    public string? ResponseId { get; init; }
    /// <summary>工具调用卡片（assistant 消息上合并携带）。</summary>
    public IReadOnlyList<ToolCallView>? ToolCalls { get; init; }
    /// <summary>是否是“排队插入”的用户消息（UI 上加特殊标记）。</summary>
    public bool Injected { get; init; }
    /// <summary>是否含图片附件（展示位，不含 base64 全量）。</summary>
    public bool HasImages { get; init; }
    /// <summary>附件数（图片数）。</summary>
    public int AttachmentCount { get; init; }
    /// <summary>毫秒时间戳。</summary>
    public long TsMs { get; init; }
}

/// <summary>一张工具卡片的数据。</summary>
public sealed record ToolCallView
{
    /// <summary>工具调用 id。</summary>
    public string Id { get; init; } = "";
    /// <summary>工具名。</summary>
    public string Name { get; init; } = "";
    /// <summary>原始 JSON 字符串（模型给的参数，可能经过截断修补）。</summary>
    public string? Args { get; init; }
    /// <summary>工具执行结果；null = 未执行或已中断。</summary>
    public bool? Ok { get; init; }
    /// <summary>工具输出（可能被截断）。</summary>
    public string? Output { get; init; }
    /// <summary>工具种类：function | unknown。缺省 function。</summary>
    public string? Kind { get; init; }
    /// <summary>原生输出类型（unknown 时展示用）。</summary>
    public string? RawType { get; init; }
    /// <summary>结构载荷（C 预留，本次置 null，原样透传）。</summary>
    public object? Payload { get; init; }
}
