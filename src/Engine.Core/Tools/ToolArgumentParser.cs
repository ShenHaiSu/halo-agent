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
    public static JsonElement Parse(string? raw)
    {
        var text = raw?.Trim() ?? "";

        // ① 空 → {}
        if (text.Length == 0) return EmptyObject();

        // ② 正常解析
        if (TryParseObject(text, out var obj)) return obj;

        // ③ 截断修补
        if (TryRepairTruncated(text, out obj)) return obj;

        var shown = text.Length > 200 ? text[..200] + "…" : text;
        throw new ToolArgumentException($"不是合法的 JSON 对象：「{shown}」");
    }

    private static bool TryParseObject(string text, out JsonElement result)
    {
        result = default;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            result = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
