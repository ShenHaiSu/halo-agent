using System.Text.Json;

namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 工具参数解析。
///
/// ============ 为什么需要它 ============
/// 弱模型经常产出这些：
///   - 空字符串 arguments（`""`）        → 归一化为 {}
///   - null                               → 归一化为 {}
///   - 数组/标量（非对象）                 → 抛 ToolArgumentException（由调用方回给模型自我修正）
///   - 缺结尾引号：`{"command":"ls -la}`   → 修补为 {"command":"ls -la"}
///   - 缺结尾花括号：`{"command":"ls -la"` → 修补为 {"command":"ls -la"}
/// 直接 throw 会让整个回合崩掉，用户看到“内部错误”。
/// 正确做法：能修就修，修不了才报错，且<b>错误信息回给模型</b>让它自我修正。
/// </summary>
public static class ToolArgumentParser
{
    private static readonly JsonDocumentOptions LenientOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static JsonElement Parse(string? raw, int? suspectedTruncationBudget = null)
    {
        var text = raw?.Trim() ?? "";

        // ① 空 → {}
        if (text.Length == 0) return EmptyObject();

        // 预处理：去 fence → 提最外层 {...}（字符串感知，不误切代码里的花括号）
        text = StripFences(text);
        text = ExtractOutermostObject(text);

        // ② 宽松解析（尾逗号/注释容忍）
        if (TryParseObject(text, out var obj, out _)) return obj;

        // 单引号：仅双引号失败才试，且不得误伤载荷里的引号/注释符号
        if (text.Contains('\'') && TryConvertSingleQuotes(text, out var converted)
            && TryParseObject(converted, out obj, out _))
        {
            System.Diagnostics.Debug.WriteLine("[ToolArgumentParser] 单引号转换成功");
            return obj;
        }

        // 截断判定优先于修补：调用方传入疑似截断预算时命中即报截断，不再硬补
        if (suspectedTruncationBudget.HasValue && text.Length >= suspectedTruncationBudget.Value)
            throw new ToolArgumentException("疑似 arguments 被截断（输出超长）。请把单次 edit 拆小后重发");
        // 无预算时的兜底启发：超长且明显未闭合即判截断（短输入仍走旧修补，不影响回归）
        if (!suspectedTruncationBudget.HasValue && text.Length >= 8000 && LooksTruncated(text))
            throw new ToolArgumentException("疑似 arguments 被截断（输出超长）。请把单次 edit 拆小后重发");

        // 裸换行：字符串内未转义字面换行是高频死因。只报行号+示例，不静默改义
        if (TryParseObject(text, out _, out var jsonEx) == false && jsonEx is not null
            && IsBareNewlineError(jsonEx))
        {
            var lineNo = jsonEx.LineNumber + 1;
            throw new ToolArgumentException(
                $"字符串内含未转义换行（第 {lineNo} 行附近）。请用 \\n 转义换行后重发，例如：" +
                "{\"path\":\"a.txt\",\"content\":\"line1\\nline2\"}");
        }

         // ③ 截断修补（旧 3 种保留）
         if (TryRepairTruncated(text, out obj))
         {
             System.Diagnostics.Debug.WriteLine("[ToolArgumentParser] arguments 为截断修补结果，长度=" + text.Length);
             return obj;
         }

        var shown = text.Length > 200 ? text[..200] + "…" : text;
        throw new ToolArgumentException($"不是合法的 JSON 对象：「{shown}」");
    }

    private static bool TryParseObject(string text, out JsonElement result)
        => TryParseObject(text, out result, out _);

    private static bool TryParseObject(string text, out JsonElement result, out JsonException? error)
    {
        result = default;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(text, LenientOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            result = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = ex;
            return false;
        }
    }

    internal static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        int firstNl = t.IndexOf('\n');
        string body = firstNl >= 0 ? t[(firstNl + 1)..] : string.Empty;
        int lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) body = body[..lastFence];
        return body.Trim();
    }

    /// <summary>
    /// 字符串感知提取最外层 {...}：跳过字符串内的大括号，防代码载荷误切。
    /// 找不到配对时原样返回（交由后续修补/报错处理）。
    /// </summary>
    internal static string ExtractOutermostObject(string text)
    {
        int first = text.IndexOf('{');
        if (first < 0) return text;
        bool inStr = false;
        bool esc = false;
        int depth = 0;
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(first, i - first + 1);
            }
        }
        return text;
    }

    internal static bool LooksTruncated(string text)
    {
        var t = text.TrimEnd();
        if (t.EndsWith('}')) return false;
        // 未闭合引号或以逗号/冒号结尾，大概率被截断
        bool inStr = false;
        bool esc = false;
        foreach (var c in t)
        {
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
        }
        if (inStr) return true;
        return t.EndsWith(',') || t.EndsWith(':') || t.EndsWith('"');
    }

    internal static bool IsBareNewlineError(JsonException ex)
    {
        var m = ex.Message ?? string.Empty;
        return m.Contains("invalid within a JSON string", StringComparison.OrdinalIgnoreCase)
            || m.Contains("'\\n'", StringComparison.Ordinal)
            || m.Contains("\"\\n\"", StringComparison.Ordinal)
            || m.Contains("LineNumber", StringComparison.Ordinal) && m.Contains('\n');
    }

    /// <summary>
    /// 单引号转双引号：仅转换作为 JSON 分隔符的单引号，字符串内的异种引号原样保留。
    /// 双引号字符串内的单引号不动；单引号字符串内的双引号转义后保留。
    /// </summary>
    internal static bool TryConvertSingleQuotes(string text, out string converted)
    {
        converted = text;
        try
        {
            var sb = new System.Text.StringBuilder(text.Length + 16);
            bool inDouble = false, inSingle = false;
            bool esc = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inDouble)
                {
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inDouble = false;
                    continue;
                }
                if (inSingle)
                {
                    if (esc) { sb.Append(c); esc = false; continue; }
                    if (c == '\\') { sb.Append(c); esc = true; continue; }
                    if (c == '\'')
                    {
                        sb.Append('"');
                        inSingle = false;
                        continue;
                    }
                    if (c == '"') sb.Append('\\');
                    sb.Append(c);
                    continue;
                }
                if (c == '"') { inDouble = true; sb.Append(c); }
                else if (c == '\'')
                {
                    // 启发：单引号后紧跟 JSON 结构字符时才视为分隔符，避免误伤缩写里的撇号
                    sb.Append('"');
                    inSingle = true;
                }
                else sb.Append(c);
            }
            if (inSingle || inDouble) return false;
            converted = sb.ToString();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 修补截断型笔误。每个候选都<b>重新解析</b>，能完整解析成对象才收。
    ///
    /// 候选顺序很重要：
    ///   1. 右花括号在但最后字符串未闭合 → 在 } 前补 "
    ///      （必须先试这个，否则补在末尾会把 } 吞进字符串值里）
    ///   2. 最后是 " 但缺 } → 补 }
    ///   3. 引号与花括号都缺 → 补 "}
    /// </summary>
    private static bool TryRepairTruncated(string text, out JsonElement result)
    {
        result = default;
        var body = text.TrimEnd();
        if (body.Length == 0) return false;

        var candidates = new List<string>(3);

        if (body.EndsWith('}'))
        {
            var head = body[..^1];
            candidates.Add(head + "\"}");
        }
        if (body.EndsWith('"'))
        {
            candidates.Add(body + "}");
        }
        candidates.Add(body + "\"}");

        foreach (var cand in candidates)
        {
            if (TryParseObject(cand, out result)) return true;
        }
        return false;
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}

public sealed class ToolArgumentException : Exception
{
    public ToolArgumentException(string message) : base(message) { }
}
