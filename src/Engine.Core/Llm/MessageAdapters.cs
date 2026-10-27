using HaloAgent.Engine.Storage.Sessions;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>
/// Storage.StoredMessage ↔ Core.ChatMessage 互转适配器（Core 侧）。
///
/// Storage 不引用 Core（分层铁律），所以适配器只能放在 Core 侧。
/// 两边线序一致（role/content/tool_calls/tool_call_id/reasoning_content），转换为浅拷贝。
/// </summary>
public static class MessageAdapters
{
    public static ChatMessage ToChatMessage(StoredMessage m) => new()
    {
        RoleWire = m.RoleWire,
        Content = m.Content,
        ToolCalls = m.ToolCalls?.Select(t => new ToolCall
        {
            Id = t.Id,
            Type = t.Type,
            Function = new FunctionCall { Name = t.Function.Name, Arguments = t.Function.Arguments },
        }).ToList(),
        ToolCallId = m.ToolCallId,
        ReasoningContent = m.ReasoningContent,
    };

    public static StoredMessage ToStoredMessage(ChatMessage m) => new()
    {
        RoleWire = m.RoleWire,
        Content = m.Content,
        ToolCalls = m.ToolCalls?.Select(t => new StoredToolCall
        {
            Id = t.Id,
            Type = t.Type,
            Function = new StoredFunctionCall { Name = t.Function.Name, Arguments = t.Function.Arguments },
        }).ToList(),
        ToolCallId = m.ToolCallId,
        ReasoningContent = m.ReasoningContent,
    };

    public static List<ChatMessage> ToChatMessages(IEnumerable<StoredMessage> messages) =>
        messages.Select(ToChatMessage).ToList();

    public static List<StoredMessage> ToStoredMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(ToStoredMessage).ToList();
}
