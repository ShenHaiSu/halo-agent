using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

/// <summary>
/// need17-02/05：条件必填（随 mode/action 变化的 required）。
/// - `edit` 分发器：mode=text→old_string/new_string；mode=lines→offset/limit/new_text（mode 缺失按 text）。
/// - `memory_tool`：action=set→name +（body 或 content 二选一，content 是 body 存量别名）。
/// 码复用 MISSING_REQUIRED_PARAM；缺 body 且缺 content 时上报缺失名为 body。
/// </summary>
public static class ConditionalRequired
{
    /// <summary>
    /// 返回指定工具在当前参数下额外要求的必填字段（已去重、保持稳定顺序）。
    /// </summary>
    public static IReadOnlyList<string> GetAdditionalRequired(string toolName, Dictionary<string, JsonElement> args)
    {
        string tool = (toolName ?? string.Empty).Trim();
        if (string.Equals(tool, "edit", StringComparison.OrdinalIgnoreCase))
        {
            return EditBranch(args);
        }
        if (string.Equals(tool, "memory_tool", StringComparison.OrdinalIgnoreCase))
        {
            return MemoryBranch(args);
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> EditBranch(Dictionary<string, JsonElement> args)
    {
        string mode = "text";
        if (args.TryGetValue("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String)
        {
            string m = (modeEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            if (m == "lines" || m == "text")
            {
                mode = m;
            }
        }

        return mode == "lines"
            ? new[] { "old_string", "new_string", "offset", "limit", "new_text" }[2..]
            : new[] { "old_string", "new_string" };
    }

    private static IReadOnlyList<string> MemoryBranch(Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
        {
            return Array.Empty<string>();
        }
        if (!string.Equals((actionEl.GetString() ?? string.Empty).Trim(), "set", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var missing = new List<string>(2);
        if (IsBlank(args, "name"))
        {
            missing.Add("name");
        }
        // body/content 二选一：仅当两者皆空才要求 body（上报名 body，handler 侧同样认 content 别名）。
        if (IsBlank(args, "body") && IsBlank(args, "content"))
        {
            missing.Add("body");
        }
        return missing;
    }

    private static bool IsBlank(Dictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var el))
        {
            return true;
        }
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }
        if (el.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(el.GetString()))
        {
            return true;
        }
        return false;
    }
}
