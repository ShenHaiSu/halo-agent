using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>四档权限模式。线上值使用 camelCase 字符串（与契约一致）。</summary>
public enum PermissionMode
{
    /// <summary>询问：写文件/执行命令都要确认。</summary>
    Ask,
    /// <summary>编辑放行：文件修改自动放行，命令仍需确认。</summary>
    AcceptEdits,
    /// <summary>全自动：全部自动执行，高危命令仍需确认。</summary>
    BypassPermissions,
    /// <summary>超级：一切放行，高危判定也跳过（极度危险）。</summary>
    Yolo,
}

/// <summary>
/// PermissionMode 的 JSON 转换器。
/// .NET 6 没有 JsonStringEnumMemberName，所以自己写。
/// 宽容：未知值回落到 Ask（不抛异常，避免一个坏帧炸掉整条流）。
/// </summary>
public sealed class PermissionModeConverter : JsonConverter<PermissionMode>
{
    /// <inheritdoc />
    public override PermissionMode Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return PermissionModeExtensions.TryParseWire(s, out var m) ? m : PermissionMode.Ask;
        }

        return PermissionMode.Ask;
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, PermissionMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWire());
    }
}

/// <summary>PermissionMode 的线上字符串 / 界面标签映射（唯一一份）。</summary>
public static class PermissionModeExtensions
{
    /// <summary>线上字符串（与 schema 的 enum 值一致）。</summary>
    public static string ToWire(this PermissionMode m) => m switch
    {
        PermissionMode.Ask => "ask",
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.BypassPermissions => "bypassPermissions",
        PermissionMode.Yolo => "yolo",
        _ => "ask",
    };

    /// <summary>界面短标签（全中文，只此一份）。</summary>
    public static string ToLabel(this PermissionMode m) => m switch
    {
        PermissionMode.Ask => "询问",
        PermissionMode.AcceptEdits => "编辑放行",
        PermissionMode.BypassPermissions => "全自动",
        PermissionMode.Yolo => "超级",
        _ => "询问",
    };

    /// <summary>解析线上字符串；未知值返回 false 且 mode 回落为 Ask。</summary>
    public static bool TryParseWire(string? raw, out PermissionMode mode)
    {
        switch (raw)
        {
            case "ask": mode = PermissionMode.Ask; return true;
            case "acceptEdits": mode = PermissionMode.AcceptEdits; return true;
            case "bypassPermissions": mode = PermissionMode.BypassPermissions; return true;
            case "yolo":
            case "YOLO": mode = PermissionMode.Yolo; return true;
            default: mode = PermissionMode.Ask; return false;
        }
    }
}
