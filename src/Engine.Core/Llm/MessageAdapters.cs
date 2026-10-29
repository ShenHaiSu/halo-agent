using HaloAgent.Engine.Storage.Sessions;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>
/// Storage.StoredMessage ↔ Core.ChatMessage 互转适配器（Core 侧）。
/// Storage 不引用 Core（分层铁律），所以适配器只能放在 Core 侧。
/// P10：双向映射 content parts；读旧文件 parts 为空即回落纯文本。
/// </summary>
public static class MessageAdapters
{
    public static ChatMessage ToChatMessage(StoredMessage m)
    {
        List<ChatContentPart>? parts = null;
        var sparts = m.ContentParts;
        if (sparts is { Count: > 0 })
        {
            parts = sparts.Select(p => p.Type == "image_url" && p.ImageUrl is not null
                ? ChatContentPart.ImagePart(p.ImageUrl.Url)
                : ChatContentPart.TextPart(p.Text ?? "")).ToList();
        }
        return new ChatMessage
        {
            RoleWire = m.RoleWire,
            Content = parts is null ? m.Content : null,
            ContentParts = parts,
            ToolCalls = m.ToolCalls?.Select(t => new ToolCall
            {
                Id = t.Id,
                Type = t.Type,
                Function = new FunctionCall { Name = t.Function.Name, Arguments = t.Function.Arguments },
            }).ToList(),
            ToolCallId = m.ToolCallId,
            ReasoningContent = m.ReasoningContent,
        };
    }

    public static StoredMessage ToStoredMessage(ChatMessage m)
    {
        var s = new StoredMessage
        {
            RoleWire = m.RoleWire,
            ToolCalls = m.ToolCalls?.Select(t => new StoredToolCall
            {
                Id = t.Id,
                Type = t.Type,
                Function = new StoredFunctionCall { Name = t.Function.Name, Arguments = t.Function.Arguments },
            }).ToList(),
            ToolCallId = m.ToolCallId,
            ReasoningContent = m.ReasoningContent,
        };
        if (m.ContentParts is { Count: > 0 })
        {
            var sparts = m.ContentParts.Select(p => p.Type == "image_url" && p.ImageUrl is not null
                ? new StoredContentPart { Type = "image_url", ImageUrl = new StoredImageUrl { Url = p.ImageUrl.Url } }
                : new StoredContentPart { Type = "text", Text = p.Text ?? "" }).ToList();
            return s.WithParts(sparts);
        }
        return s.WithContent(m.Content);
    }

    public static List<ChatMessage> ToChatMessages(IEnumerable<StoredMessage> messages) =>
        messages.Select(ToChatMessage).ToList();

    public static List<StoredMessage> ToStoredMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(ToStoredMessage).ToList();

    /// <summary>发送前剥离 reasoning（拷贝投影，不 mutate 落盘）。includeHistory=false 时置 null。</summary>
    public static IReadOnlyList<ChatMessage> StripReasoning(IReadOnlyList<ChatMessage> messages, bool includeHistory)
    {
        if (includeHistory) return messages;
        return messages.Select(m => m.ReasoningContent is null ? m : m with { ReasoningContent = null }).ToList();
    }

    /// <summary>历史膨胀 guard：单消息图片数与总字节上限。</summary>
    public const int MaxImagesPerMessage = 8;
    public const int MaxImageBytesPerMessage = 8 * 1024 * 1024;

    public static void ValidateImages(ChatMessage m, bool acceptsImages)
    {
        if (!m.HasImages) return;
        if (!acceptsImages)
            throw new InvalidOperationException("当前模型不支持图片（accepts_images=false），已拦截含图消息而非静默丢图。");
        var imgs = m.ContentParts!.Where(p => p.Type == "image_url").ToList();
        if (imgs.Count > MaxImagesPerMessage)
            throw new InvalidOperationException($"单消息图片数超限（{imgs.Count}>{MaxImagesPerMessage}）");
        long total = 0;
        foreach (var img in imgs) total += img.ImageUrl?.Url.Length ?? 0;
        if (total > MaxImageBytesPerMessage)
            throw new InvalidOperationException($"单消息图片总字节超限（>{MaxImageBytesPerMessage}）");
    }

    /// <summary>总结/压缩请求 strip images：只留 [图片xN] 占位文本（防无图能力模型 400）。</summary>
    public static ChatMessage StripImagesForSummary(ChatMessage m)
    {
        if (!m.HasImages) return m;
        var n = m.ContentParts!.Count(p => p.Type == "image_url");
        var texts = m.ContentParts!.Where(p => p.Type == "text").Select(p => p.Text ?? "");

        var joined = string.Join("\n", texts);
        var placeholder = $"[图片x{n}]";
        var content = string.IsNullOrWhiteSpace(joined) ? placeholder : joined + "\n" + placeholder;
        return m with { Content = content, ContentParts = null };
    }
}
