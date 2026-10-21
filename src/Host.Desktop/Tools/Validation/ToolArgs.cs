using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

/// <summary>
/// need22-P0 S3：归一化参数单次解析包（各 Handler 统一入口，消除重复 JsonDocument.Parse 双轨）。
/// ToolRegistry.DispatchAsync 已把 ToolCallValidator.Validate 产出的 NormalizedJson 回填进
/// execCall.ArgumentsJson；Handler 只需 Parse 一次即为归一化参数。
/// </summary>
public sealed class ToolArgs : IDisposable
{
    private readonly JsonDocument _doc;

    private ToolArgs(JsonDocument doc)
    {
        _doc = doc;
    }

    /// <summary>根元素（非对象时为 Undefined 兼容值）。</summary>
    public JsonElement Root => _doc.RootElement;

    /// <summary>解析（空串→{}；失败抛 JsonException，调用方转自愈提示）。</summary>
    public static ToolArgs Parse(string? argumentsJson)
    {
        var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson
        );
        return new ToolArgs(doc);
    }

    public string? GetString(string name)
    {
        if (
            Root.ValueKind == JsonValueKind.Object
            && Root.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.String
        )
        {
            return el.GetString();
        }

        return null;
    }

    public bool TryGetInt32(string name, out int value)
    {
        value = 0;
        if (
            Root.ValueKind == JsonValueKind.Object
            && Root.TryGetProperty(name, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int v)
        )
        {
            value = v;
            return true;
        }

        return false;
    }

    public bool TryGetBool(string name, out bool value)
    {
        value = false;
        if (
            Root.ValueKind == JsonValueKind.Object
            && Root.TryGetProperty(name, out JsonElement el)
            && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
        )
        {
            value = el.GetBoolean();
            return true;
        }

        return false;
    }

    public void Dispose() => _doc.Dispose();
}
