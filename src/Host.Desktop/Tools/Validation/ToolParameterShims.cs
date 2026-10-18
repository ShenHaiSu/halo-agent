using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

/// <summary>
/// need17-02：Shim 表（各工具一行，无分支膨胀）。只做无歧义修复（别名、大小写、数字字符串、时间后缀）；
/// 有歧义的一律不动，交 Validator 拒绝并指明改法。
/// 警告码：ALIASED_PARAM（别名搬运，冻结）/ DEFAULTED_PARAM（缺省补齐）/ COERCED_TYPE（宽容转换）。
/// </summary>
public static class ToolParameterShims
{
    public sealed class ShimResult
    {
        public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; init; } = new();
    }

    public static ShimResult ApplyShim(string toolName, Dictionary<string, JsonElement> args)
    {
        var dict = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
        var warnings = new List<string>();
        string tool = (toolName ?? string.Empty).Trim();

        // 全工具：path(string)→file_path 搬运（后者缺失时）。
        if (dict.TryGetValue("path", out var pathEl)
            && pathEl.ValueKind == JsonValueKind.String
            && (!dict.TryGetValue("file_path", out var fpEl) || fpEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fpEl.GetString())))
        {
            dict["file_path"] = pathEl;
            warnings.Add("ALIASED_PARAM：别名 'path' 已搬运为 'file_path'，请改用 'file_path'。");
        }

        if (tool.Equals("exec_command", StringComparison.OrdinalIgnoreCase))
        {
            // cmd→command。
            if (dict.TryGetValue("cmd", out var cmdEl)
                && (!dict.TryGetValue("command", out var cmdHas) || cmdHas.ValueKind != JsonValueKind.String))
            {
                dict["command"] = cmdEl;
                warnings.Add("ALIASED_PARAM：别名 'cmd' 已搬运为 'command'，请改用 'command'。");
            }
            // timeout/timeoutMs→timeout_ms；"30s"→30000（支持 s/ms 后缀）。
            JsonElement? timeoutSrc = null;
            if (dict.TryGetValue("timeout", out var t1))
            {
                timeoutSrc = t1;
            }
            else if (dict.TryGetValue("timeoutMs", out var t2))
            {
                timeoutSrc = t2;
            }
            if (timeoutSrc.HasValue && (!dict.TryGetValue("timeout_ms", out _)))
            {
                long? ms = ParseTimeoutMs(timeoutSrc.Value);
                if (ms.HasValue)
                {
                    dict["timeout_ms"] = JsonDocument.Parse(ms.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();
                    warnings.Add("ALIASED_PARAM：别名 'timeout'/'timeoutMs' 已归一为 'timeout_ms'（毫秒），请改用 'timeout_ms'。");
                }
            }
        }

        if (tool.Equals("edit_file", StringComparison.OrdinalIgnoreCase))
        {
            // 小驼峰→蛇形（无歧义）。
            Rename(dict, warnings, "oldString", "old_string");
            Rename(dict, warnings, "newString", "new_string");
            // old_string 为 JSON 字符串且能解析→保持原样交业务层（不猜），此处不做任何处理。
        }

        if (tool.Equals("write_file", StringComparison.OrdinalIgnoreCase))
        {
            Rename(dict, warnings, "allowOverwrite", "allow_overwrite");
            // content 非 string（如数字）→拒（不做 ToString 猜），此处不动，交 Validator 报 TYPE_MISMATCH。
        }

        if (tool.Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            // 缺 mode→默认 text + warning（老调用不断）；mode 非法→ENUM_VIOLATION（Validator 统一拒）。
            if (!dict.TryGetValue("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(modeEl.GetString()))
            {
                dict["mode"] = JsonDocument.Parse("\"text\"").RootElement.Clone();
                warnings.Add("DEFAULTED_PARAM：缺失 'mode'，已默认按 'text'（edit_file 引擎）执行；请显式传 mode。");
            }
        }

        return new ShimResult { Args = dict, Warnings = warnings };
    }

    private static void Rename(Dictionary<string, JsonElement> dict, List<string> warnings, string from, string to)
    {
        if (dict.TryGetValue(from, out var el)
            && (!dict.TryGetValue(to, out var has) || has.ValueKind == JsonValueKind.Null || has.ValueKind == JsonValueKind.Undefined))
        {
            dict[to] = el;
            warnings.Add($"ALIASED_PARAM：别名 '{from}' 已搬运为 '{to}'，请改用 '{to}'。");
        }
    }

    /// <summary>解析超时：数字=毫秒；字符串支持纯数字 / "30s" / "300ms"。</summary>
    internal static long? ParseTimeoutMs(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt64(out long v))
            {
                return v;
            }
            if (el.TryGetDouble(out double d) && d >= 0 && d == Math.Floor(d))
            {
                return (long)d;
            }
            return null;
        }
        if (el.ValueKind == JsonValueKind.String)
        {
            string s = (el.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            if (long.TryParse(s, out long plain))
            {
                return plain;
            }
            if (s.EndsWith("ms") && long.TryParse(s[..^2].Trim(), out long ms))
            {
                return ms;
            }
            if (s.EndsWith("s") && double.TryParse(s[..^1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec))
            {
                return (long)(sec * 1000);
            }
        }
        return null;
    }
}
