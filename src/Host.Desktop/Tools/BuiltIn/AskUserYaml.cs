using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

public sealed class AskUserParseException : Exception
{
    public AskUserParseException(string message)
        : base(message) { }
}

/// <summary>
/// need09-06：YAML 最小子集（key: value + "- " 列表 + "|" 字面块 + "#" 注释）。
/// 覆盖模型手写问卷糖；解析失败抛 AskUserParseException（上层转自愈 JSON 模板）。
/// </summary>
public static class MiniYamlParser
{
    public static List<AskUserQuestion> ParseQuestions(string yaml)
    {
        var root = ParseDocument(yaml ?? string.Empty);
        object? node = root;
        if (node is Dictionary<string, object?> map)
        {
            if (map.TryGetValue("questions", out var q))
                node = q;
            else if (map.TryGetValue("question", out var single))
                node = new List<object?> { single };
        }
        if (node is List<object?> list)
        {
            var out2 = new List<AskUserQuestion>();
            int i = 0;
            foreach (var item in list)
            {
                i++;
                if (item is Dictionary<string, object?> m)
                    out2.Add(ToQuestion(m, i));
                else
                    throw new AskUserParseException(
                        $"YAML 第 {i} 题必须是映射（id/question/options）。自愈模板：{{\"questions\":[{{\"id\":\"q1\",\"question\":\"你的问题\"}}]}}"
                    );
            }
            if (out2.Count == 0)
                throw new AskUserParseException("YAML 中未找到 questions 列表。");
            return out2;
        }
        throw new AskUserParseException(
            "YAML 解析失败或结构不符（需要 questions: 列表）。自愈模板：{\"questions\":[{\"id\":\"q1\",\"question\":\"你的问题\"}]}"
        );
    }

    internal static Dictionary<string, object?> ParseDocument(string yaml)
    {
        var lines = Preprocess(yaml);
        int pos = 0;
        var root = ParseMap(lines, ref pos, -1);
        return root;
    }

    private static List<(int Indent, string Text)> Preprocess(string yaml)
    {
        var result = new List<(int, string)>();
        foreach (string raw in (yaml ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
                indent++;
            string text = line.Substring(indent);
            if (text.StartsWith("#"))
                continue;
            text = StripTrailingComment(text);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            result.Add((indent, text.TrimEnd()));
        }
        return result;
    }

    private static string StripTrailingComment(string text)
    {
        bool inSingle = false,
            inDouble = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (c == '"' && !inSingle)
                inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && i > 0 && char.IsWhiteSpace(text[i - 1]))
                return text.Substring(0, i).TrimEnd();
        }
        return text;
    }

    private static Dictionary<string, object?> ParseMap(
        List<(int Indent, string Text)> lines,
        ref int pos,
        int parentIndent
    )
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        while (pos < lines.Count && lines[pos].Indent > parentIndent)
        {
            var (indent, text) = lines[pos];
            if (text.StartsWith("- "))
                break;
            int colon = FindKeyColon(text);
            if (colon < 0)
            {
                pos++;
                continue;
            }
            string key = text.Substring(0, colon).Trim().Trim('"', '\'');
            string rest = text.Substring(colon + 1).Trim();
            pos++;
            if (rest == "|" || rest == ">")
            {
                map[key] = ReadLiteralBlock(lines, ref pos, indent);
            }
            else if (rest.Length > 0)
            {
                map[key] = Unquote(rest);
            }
            else
            {
                if (pos < lines.Count && lines[pos].Indent > indent)
                {
                    if (lines[pos].Text.StartsWith("- "))
                        map[key] = ParseList(lines, ref pos, indent);
                    else
                        map[key] = ParseMap(lines, ref pos, indent);
                }
                else
                    map[key] = string.Empty;
            }
        }
        return map;
    }

    private static List<object?> ParseList(
        List<(int Indent, string Text)> lines,
        ref int pos,
        int parentIndent
    )
    {
        var list = new List<object?>();
        while (
            pos < lines.Count
            && lines[pos].Indent > parentIndent
            && lines[pos].Text.StartsWith("- ")
        )
        {
            var (indent, text) = lines[pos];
            string item = text.Substring(2).Trim();
            pos++;
            if (item.Length == 0)
            {
                if (pos < lines.Count && lines[pos].Indent > indent)
                {
                    if (lines[pos].Text.StartsWith("- "))
                        list.Add(ParseList(lines, ref pos, indent));
                    else
                        list.Add(ParseMap(lines, ref pos, indent));
                }
                else
                    list.Add(string.Empty);
            }
            else
            {
                int colon = FindKeyColon(item);
                if (colon >= 0)
                {
                    var m = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    m[item.Substring(0, colon).Trim().Trim('"', '\'')] = Unquote(
                        item.Substring(colon + 1).Trim()
                    );
                    if (
                        pos < lines.Count
                        && lines[pos].Indent > indent
                        && !lines[pos].Text.StartsWith("- ")
                    )
                    {
                        var cont = ParseMap(lines, ref pos, indent);
                        foreach (var kv in cont)
                            m[kv.Key] = kv.Value;
                    }
                    list.Add(m);
                }
                else
                    list.Add(Unquote(item));
            }
        }
        return list;
    }

    private static string ReadLiteralBlock(
        List<(int Indent, string Text)> lines,
        ref int pos,
        int parentIndent
    )
    {
        var sb = new StringBuilder();
        while (pos < lines.Count && lines[pos].Indent > parentIndent)
        {
            sb.AppendLine(lines[pos].Text);
            pos++;
        }
        return sb.ToString().TrimEnd('\n', '\r');
    }

    private static int FindKeyColon(string text)
    {
        bool inSingle = false,
            inDouble = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (c == '"' && !inSingle)
                inDouble = !inDouble;
            else if (
                c == ':'
                && !inSingle
                && !inDouble
                && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || text[i + 1] == '#')
            )
                return i;
        }
        return -1;
    }

    private static string Unquote(string s)
    {
        s = (s ?? string.Empty).Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s.Substring(1, s.Length - 2);
        return s;
    }

    private static AskUserQuestion ToQuestion(Dictionary<string, object?> m, int index)
    {
        string id = m.TryGetValue("id", out var idO)
            ? (idO?.ToString() ?? string.Empty).Trim()
            : string.Empty;
        if (string.IsNullOrEmpty(id))
            id = "q" + index;
        string question = m.TryGetValue("question", out var qO)
            ? (qO?.ToString() ?? string.Empty).Trim()
            : string.Empty;
        var q = new AskUserQuestion { Id = id, Question = question };
        if (m.TryGetValue("multiSelect", out var msO))
            q.MultiSelect = IsTrue(msO);
        if (m.TryGetValue("allowCustom", out var acO))
            q.AllowCustom = IsTrue(acO);
        if (m.TryGetValue("default", out var dO))
            q.Default = dO?.ToString();
        if (m.TryGetValue("options", out var oO) && oO is List<object?> opts)
        {
            foreach (var o in opts)
            {
                if (o is string s && !string.IsNullOrWhiteSpace(s))
                    q.Options.Add(new AskUserOption { Label = s.Trim() });
                else if (o is Dictionary<string, object?> om)
                {
                    string label = om.TryGetValue("label", out var lO)
                        ? (lO?.ToString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (string.IsNullOrEmpty(label))
                        continue;
                    q.Options.Add(
                        new AskUserOption
                        {
                            Label = label,
                            Description = om.TryGetValue("description", out var deO)
                                ? deO?.ToString()
                                : null,
                        }
                    );
                }
            }
        }
        return q;
    }

    private static bool IsTrue(object? v)
    {
        string s = (v?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
        return s is "true" or "yes" or "1" or "on";
    }
}
