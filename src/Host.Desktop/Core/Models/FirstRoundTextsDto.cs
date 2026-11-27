namespace Host.Desktop.Core.Models;

/// <summary>
/// 标题链路首轮取数 DTO（后端直读摘要层，与消息门无关；自动/手动标题共用）。
/// CefSharp 序列化后前端经 normalizeBridgePayload 转为 camelCase（firstUser/assistant/userCount）。
/// </summary>
public sealed class FirstRoundTextsDto
{
    public string FirstUser { get; init; } = string.Empty;
    public string Assistant { get; init; } = string.Empty;
    public int UserCount { get; init; }
}
