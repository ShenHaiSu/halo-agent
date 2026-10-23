using System.Text;
using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

public static partial class ToolCallValidator
{
    private sealed class FieldCheck
    {
        public ToolCallValidationResult? Error;
        public List<string> Warnings = new();
    }

    private static FieldCheck CheckField(
        string tool,
        ToolParamSpec ps,
        JsonElement el,
        Dictionary<string, JsonElement> dict
    )
    {
        var fc = new FieldCheck();
        string summary() => Summarize(SerializeArgs(dict));

        switch (ps.Type)
        {
            case "integer":
            {
                long v;
                if (el.ValueKind == JsonValueKind.Number)
                {
                    if (el.TryGetInt64(out v))
                    {
                        // 原生整数：通过。
                    }
                    else if (
                        el.TryGetDouble(out double d)
                        && d == Math.Floor(d)
                        && d >= long.MinValue
                        && d <= long.MaxValue
                    )
                    {
                        v = (long)d;
                        dict[ps.Name] = JsonDocument
                            .Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .RootElement.Clone();
                        fc.Warnings.Add(
                            $"COERCED_TYPE：参数 '{ps.Name}' 的小数 {el.GetRawText()} 恰为整数，已取整为 {v}。"
                        );
                    }
                    else
                    {
                        fc.Error = Fail(
                            Codes.TypeMismatch,
                            $"参数 '{ps.Name}' 应为 integer，实际收到 number {el.GetRawText()}（非整数）。收到的参数：{summary()}。请传整数后重试。",
                            tool,
                            new { tool, param = ps.Name }
                        );
                        return fc;
                    }
                }
                else if (el.ValueKind == JsonValueKind.String)
                {
                    string s = (el.GetString() ?? string.Empty).Trim();
                    if (
                        long.TryParse(
                            s,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out v
                        )
                    )
                    {
                        // 数字字符串→数字（宽容）。
                    }
                    else if (
                        double.TryParse(
                            s,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double d
                        )
                        && d == Math.Floor(d)
                        && d >= long.MinValue
                        && d <= long.MaxValue
                    )
                    {
                        v = (long)d;
                    }
                    else
                    {
                        fc.Error = Fail(
                            Codes.TypeMismatch,
                            $"参数 '{ps.Name}' 应为 integer，实际收到 string \"{Trunc(s, 80)}\"。请传数字后重试。收到的参数：{summary()}。",
                            tool,
                            new { tool, param = ps.Name }
                        );
                        return fc;
                    }
                    dict[ps.Name] = JsonDocument
                        .Parse(v.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .RootElement.Clone();
                    fc.Warnings.Add(
                        $"COERCED_TYPE：参数 '{ps.Name}' 的字符串 \"{Trunc(s, 80)}\" 已宽容转换为数字 {v}，请直接传数字。"
                    );
                }
                else
                {
                    fc.Error = Fail(
                        Codes.TypeMismatch,
                        $"参数 '{ps.Name}' 应为 integer，实际收到 {KindName(el)}。收到的参数：{summary()}。请传数字后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                    return fc;
                }
                if (ps.Minimum.HasValue && v < ps.Minimum.Value)
                {
                    fc.Error = Fail(
                        Codes.ConstraintViolation,
                        $"参数 '{ps.Name}' 最小为 {ps.Minimum.Value}，实际收到 {v}。收到的参数：{summary()}。请修正后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                }
                else if (ps.Maximum.HasValue && v > ps.Maximum.Value)
                {
                    fc.Error = Fail(
                        Codes.ConstraintViolation,
                        $"参数 '{ps.Name}' 最大为 {ps.Maximum.Value}，实际收到 {v}。收到的参数：{summary()}。请修正后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                }
                break;
            }
            case "boolean":
            {
                if (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
                {
                    break;
                }
                if (
                    el.ValueKind == JsonValueKind.String
                    && bool.TryParse((el.GetString() ?? string.Empty).Trim(), out bool b)
                )
                {
                    dict[ps.Name] = JsonDocument.Parse(b ? "true" : "false").RootElement.Clone();
                    fc.Warnings.Add(
                        $"COERCED_TYPE：参数 '{ps.Name}' 的字符串 \"{el.GetString()}\" 已宽容转换为布尔 {b.ToString().ToLowerInvariant()}，请直接传布尔。"
                    );
                }
                else
                {
                    fc.Error = Fail(
                        Codes.TypeMismatch,
                        $"参数 '{ps.Name}' 应为 boolean，实际收到 {DescribeValue(el)}。收到的参数：{summary()}。请传 true/false 后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                }
                break;
            }
            case "string":
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    fc.Error = Fail(
                        Codes.TypeMismatch,
                        $"参数 '{ps.Name}' 应为 string，实际收到 {DescribeValue(el)}。收到的参数：{summary()}。请传字符串后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                    return fc;
                }
                string s = el.GetString() ?? string.Empty;
                if (
                    ps.EnumValues != null
                    && ps.EnumValues.Length > 0
                    && !ps.EnumValues.Contains(s, StringComparer.Ordinal)
                )
                {
                    fc.Error = Fail(
                        Codes.EnumViolation,
                        $"参数 '{ps.Name}' 取值非法：'{Trunc(s, 80)}'。可选值：{string.Join(" | ", ps.EnumValues)}。收到的参数：{summary()}。请修正后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                    return fc;
                }
                if (ps.MinLength.HasValue && s.Length < ps.MinLength.Value)
                {
                    fc.Error = Fail(
                        Codes.ConstraintViolation,
                        $"参数 '{ps.Name}' 长度至少 {ps.MinLength.Value} 字符，实际 {s.Length} 字符。收到的参数：{summary()}。请加长该参数后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                }
                else if (ps.MaxLength.HasValue && s.Length > ps.MaxLength.Value)
                {
                    fc.Error = Fail(
                        Codes.ConstraintViolation,
                        $"参数 '{ps.Name}' 长度至多 {ps.MaxLength.Value} 字符，实际 {s.Length} 字符。收到的参数：{summary()}。请缩短该参数后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                }
                break;
            }
            case "array":
            {
                if (el.ValueKind != JsonValueKind.Array)
                {
                    fc.Error = Fail(
                        Codes.TypeMismatch,
                        $"参数 '{ps.Name}' 应为 array，实际收到 {DescribeValue(el)}。收到的参数：{summary()}。请传数组后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                }
                break;
            }
            case "object":
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    fc.Error = Fail(
                        Codes.TypeMismatch,
                        $"参数 '{ps.Name}' 应为 object，实际收到 {DescribeValue(el)}。收到的参数：{summary()}。请传对象后重试。",
                        tool,
                        new { tool, param = ps.Name }
                    );
                }
                break;
            }
        }
        return fc;
    }

    private static bool IsMissing(
        Dictionary<string, JsonElement> dict,
        string name,
        bool exceptEmptyString
    )
    {
        if (!dict.TryGetValue(name, out var el))
        {
            return true;
        }
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }
        if (
            !exceptEmptyString
            && el.ValueKind == JsonValueKind.String
            && string.IsNullOrWhiteSpace(el.GetString())
        )
        {
            return true;
        }
        return false;
    }

    private static ToolCallValidationResult Fail(
        string code,
        string message,
        string tool,
        object? payload
    )
    {
        string? payloadJson = payload == null ? null : JsonSerializer.Serialize(payload);
        return new ToolCallValidationResult
        {
            Success = false,
            Code = code,
            Message = message,
            PayloadJson = payloadJson,
        };
    }

    /// <summary>need17-02：截断摘要（超 500 字符截断 + ...，避免错误消息本身爆上下文）。</summary>
    public static string Summarize(string? json, int maxChars = SummaryMaxChars)
    {
        string s = json ?? string.Empty;
        if (s.Length > maxChars)
        {
            return s.Substring(0, maxChars) + "...";
        }
        return s;
    }

    internal static string SerializeArgs(Dictionary<string, JsonElement> dict)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var kv in dict)
            {
                w.WritePropertyName(kv.Key);
                kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string KindName(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => el.ValueKind.ToString().ToLowerInvariant(),
        };

    private static string DescribeValue(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => $"string \"{Trunc(el.GetString() ?? string.Empty, 80)}\"",
            JsonValueKind.Number => $"number {el.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => $"boolean {el.GetRawText()}",
            JsonValueKind.Array => $"array（{el.GetArrayLength()} 项）",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => KindName(el),
        };

    private static string Trunc(string s, int max) =>
        s.Length > max ? s.Substring(0, max) + "..." : s;

    private sealed class ShimResult
    {
        public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; init; } = new();
    }

    private static ShimResult ApplyShim(string toolName, Dictionary<string, JsonElement> args)
    {
        var dict = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
        var warnings = new List<string>();
        string tool = (toolName ?? string.Empty).Trim();

        // 全工具：path(string)→file_path 搬运（后者缺失时）。
        if (
            dict.TryGetValue("path", out var pathEl)
            && pathEl.ValueKind == JsonValueKind.String
            && (
                !dict.TryGetValue("file_path", out var fpEl)
                || fpEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(fpEl.GetString())
            )
        )
        {
            dict["file_path"] = pathEl;
            warnings.Add("ALIASED_PARAM：别名 'path' 已搬运为 'file_path'，请改用 'file_path'。");
        }

        if (tool.Equals("exec_command", StringComparison.OrdinalIgnoreCase))
        {
            // cmd→command。
            if (
                dict.TryGetValue("cmd", out var cmdEl)
                && (
                    !dict.TryGetValue("command", out var cmdHas)
                    || cmdHas.ValueKind != JsonValueKind.String
                )
            )
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
                    dict["timeout_ms"] = JsonDocument
                        .Parse(ms.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .RootElement.Clone();
                    warnings.Add(
                        "ALIASED_PARAM：别名 'timeout'/'timeoutMs' 已归一为 'timeout_ms'（毫秒），请改用 'timeout_ms'。"
                    );
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
            if (
                !dict.TryGetValue("mode", out var modeEl)
                || modeEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(modeEl.GetString())
            )
            {
                dict["mode"] = JsonDocument.Parse("\"text\"").RootElement.Clone();
                warnings.Add("DEFAULTED_PARAM：缺失 'mode'，已默认按 'text'（edit_file 引擎）执行；请显式传 mode。");
            }
        }

        return new ShimResult { Args = dict, Warnings = warnings };
    }

    private static void Rename(
        Dictionary<string, JsonElement> dict,
        List<string> warnings,
        string from,
        string to
    )
    {
        if (
            dict.TryGetValue(from, out var el)
            && (
                !dict.TryGetValue(to, out var has)
                || has.ValueKind == JsonValueKind.Null
                || has.ValueKind == JsonValueKind.Undefined
            )
        )
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
            if (
                s.EndsWith("s")
                && double.TryParse(
                    s[..^1].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double sec
                )
            )
            {
                return (long)(sec * 1000);
            }
        }
        return null;
    }
}
