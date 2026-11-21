namespace Host.Desktop.Core.Models;

/// <summary>
/// need03-03-B：标题链路首轮取数 DTO（后端直读存储，不受 SessionBridge.getSessionMessages
/// 的 HistoryVersion==2 守卫影响；自动/手动标题共用）。
/// CefSharp 序列化后前端经 normalizeBridgePayload 转为 camelCase（firstUser/assistant/userCount）。
/// </summary>
public sealed class FirstRoundTextsDto
{
    public string FirstUser { get; init; } = string.Empty;
    public string Assistant { get; init; } = string.Empty;
    public int UserCount { get; init; }
}
