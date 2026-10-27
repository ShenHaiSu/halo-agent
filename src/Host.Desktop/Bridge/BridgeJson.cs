using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloAgent.Desktop.Bridge;

/// <summary>
/// 桥接序列化选项：camelCase + 字符串枚举。
/// 注意：CefSharp DefaultBinder 对方法返回值 DTO 默认保持 C# 原名（P0 探针结论），
/// 这里的 Options 用于事件推送 JSON 与 AgentBridge 参数回序列化。
/// </summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
