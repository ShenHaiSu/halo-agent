using System.Text.Json;

namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 读工具参数的扩展方法（供各工具读 JsonElement 用）。
/// 缺失 / 类型不符一律返回 null，由调用方按“参数不能为空”报错。
/// </summary>
public static class JsonHelpers
{
    public static string? GetStringOrNull(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static long? GetLongOrNull(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetInt64(out var n) ? n : null;
            case JsonValueKind.String:
                return long.TryParse(v.GetString(), out var s) ? s : null;
            default:
                return null;
        }
    }
}
