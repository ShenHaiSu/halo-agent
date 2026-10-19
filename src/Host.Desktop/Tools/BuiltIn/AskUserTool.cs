 using System.Diagnostics;
 using System.Text;
 using System.Text.Json;
 using Host.Desktop.Infrastructure.Logging;
 using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need09-06：ask_user 阻塞式问询工具。
/// 模型传 questions（1-4 题，选项 0-6 个）→ Publish agent:ask-user-requested → 阻塞等前端 resolveAskUser；
/// 超时/取消转 Fail 不挂起；第二并发直接 Fail。返回 answers + 摘要（500 截断）+ json 块。
/// </summary>
public sealed class AskUserTool : IToolHandler
{
    public const int SummaryMaxChars = 500;
    public const int CustomMaxChars = 2000;

    public string Name => "ask_user";

    // NEED17-05 §5.1: Exposure=Direct（编码任务默认不打扰；激活集/preset 裁剪由基础组 01 落子，本文件只定声明）。
    public const string Need17Exposure = "Direct";
    // NEED17-05 §5.3: 供 06 动态装配的 PromptBrief/PromptRules（ToolDefinition 字段待基础组 01 扩展，此处先以常量承载，装配表见 SystemPromptBuilder）。
    public const string PromptBrief = "Ask the user a blocking question";
    public static readonly string[] PromptRules = { "Ask only when blocked; never for progress updates." };

    public string Description =>
        "Ask the user blocking questions (1-4) with optional single-choice or multi-choice options. " +
        "The turn pauses until the user answers in the UI, times out, or cancels. " +
        "Use when a choice blocks progress (destructive ops, ambiguous requirements). Do not use for status reports.";

    public ToolDefinition GetDefinition() => new(Name, Description,
         new ToolSchemaBuilder()
             .AddString("question", "The blocking question to ask. Required, non-empty. Use when a choice blocks progress (destructive ops, ambiguous requirements). Do not use for status reports.", required: true, minLength: 1)
             .AddArray("questions", "1-4 questions. Each: {id, question, options?[{label, description?}], multiSelect?, allowCustom?, default?}. Empty options means open question.", itemsType: "object")
             .AddString("questionsYaml", "Minimal-YAML shortcut for questions (same shape as questions).")
             .AddInteger("timeoutSec", "Wait timeout seconds, 1-3600. Defaults to 300.", minimum: 1, maximum: 3600)
             .Build());

    public async Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // need13：子代理禁止 ask_user（v1 锁定，首行拒，未进 gate）
        if (string.Equals(context.AgentRole, "sub", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail("[SUBAGENT_ASK_DENIED] 子代理禁止 ask_user（v1 锁定）。本次已拒绝，不会弹窗。请按保守默认继续，并在总结中注明假设，交用户在主会话确认。",
                durationMs: sw.ElapsedMilliseconds);
        try
        {
            string sessionId = (context.SessionId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return ToolResult.Fail("ask_user 需要会话上下文（SessionId 为空），无法挂起等待用户。",
                    durationMs: sw.ElapsedMilliseconds);
            }
            List<AskUserQuestion>? questions;
            int timeoutSec = AskUserGate.DefaultTimeoutSec;
            try
            {
                (questions, timeoutSec) = ParseArguments(call.ArgumentsJson, timeoutSec);
            }
            catch (AskUserParseException pex)
            {
                return ToolResult.Fail(pex.Message, durationMs: sw.ElapsedMilliseconds);
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail(
                    $"参数 JSON 解析失败：{ex.Message}。正确示例：{{\"questions\":[{{\"id\":\"q1\",\"question\":\"用哪个框架？\",\"options\":[{{\"label\":\"React\"}}]}}]}}。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            string? validationError = ValidateQuestions(questions);
            if (validationError != null)
                return ToolResult.Fail(validationError, durationMs: sw.ElapsedMilliseconds);

            var (created, ticket, conflict) = AskUserGate.TryCreate(sessionId, call.Id ?? string.Empty, questions!);
            if (!created)
            {
                return ToolResult.Fail(
                    $"已有待回答的提问（ticket={conflict}），请等待用户先回答上一组问题；本次提问已拒绝，不会重复弹窗。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            AskUserGate.PublishRequested(sessionId, ticket, call.Id ?? string.Empty, questions!);

            int waitSec = Math.Clamp(timeoutSec, 1, 3600);
            var pending = AskUserGate.GetPending(sessionId);
            if (pending == null)
            {
                return ToolResult.Fail("提问挂起失败（pending 丢失），请重试。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            AskUserOutcome outcome;
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.CancellationToken);
                var waitTask = pending.Tcs.Task;
                var completed = await Task.WhenAny(
                    waitTask,
                    Task.Delay(TimeSpan.FromSeconds(waitSec), linkedCts.Token)).ConfigureAwait(false);
                if (completed != waitTask)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    AskUserGate.RemoveTimeout(sessionId, ticket, "timeout");
                    return ToolResult.Fail(
                        $"等待用户回答超时（{waitSec}s），提问已自动取消，不再挂起。请按保守默认继续，必要时精简问题后重新提问。",
                        durationMs: sw.ElapsedMilliseconds);
                }
                outcome = await waitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                AskUserGate.RemoveTimeout(sessionId, ticket, "cancelled");
                return ToolResult.Fail(
                    "提问已被取消（用户取消提问/停止生成），请按保守默认继续，不要重复追问。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            if (!outcome.Resolved)
            {
                return ToolResult.Fail(
                    "提问未完成（超时或取消），请按保守默认继续，不要重复追问。",
                    durationMs: sw.ElapsedMilliseconds);
            }

            string output = BuildOutput(questions!, outcome.AnswersJson);
            return ToolResult.Ok(output, durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"ask_user 执行异常：{ex.Message}。请修正参数后重试。",
                durationMs: sw.ElapsedMilliseconds);
        }
        finally
        {
            sw.Stop();
        }
    }

    public static (List<AskUserQuestion> Questions, int TimeoutSec) ParseArguments(string? argsJson, int defaultTimeout)
    {
        string raw = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson.Trim();
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement root = doc.RootElement;
        int timeout = defaultTimeout;
        if (root.ValueKind == JsonValueKind.Array)
            return (ParseQuestionsArray(root, 0), timeout);
        if (root.ValueKind != JsonValueKind.Object)
            throw new AskUserParseException("参数必须是对象 {questions:[...]} 或问题数组。请按 JSON 模板重试。");

        if (root.TryGetProperty("timeoutSec", out var tEl) && tEl.ValueKind == JsonValueKind.Number && tEl.TryGetInt32(out int t))
            timeout = t;

        if (root.TryGetProperty("questions", out var qEl))
        {
            if (qEl.ValueKind == JsonValueKind.Array)
                return (ParseQuestionsArray(qEl, 0), timeout);
            if (qEl.ValueKind == JsonValueKind.String)
            {
                string s = (qEl.GetString() ?? string.Empty).Trim();
                if (s.StartsWith("[") || s.StartsWith("{"))
                {
                    using JsonDocument inner = JsonDocument.Parse(s);
                    if (inner.RootElement.ValueKind == JsonValueKind.Array)
                        return (ParseQuestionsArray(inner.RootElement, 0), timeout);
                    return (new List<AskUserQuestion> { ParseQuestion(inner.RootElement, 1) }, timeout);
                }
                return (MiniYamlParser.ParseQuestions(s), timeout);
            }
            throw new AskUserParseException("questions 必须是数组或 YAML 字符串。失败自愈模板：{\"questions\":[{\"id\":\"q1\",\"question\":\"你的问题\",\"options\":[]}]}");
        }
        // NEED17-05 §5.2: Schema 主推 string question（required/minLength:1）；object 别名保留兼容（执行语义不变）。
        if (root.TryGetProperty("question", out var single))
        {
            if (single.ValueKind == JsonValueKind.String)
            {
                string text = (single.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(text))
                    throw new AskUserParseException("question 不能为空（minLength:1）。请给出具体的阻塞问题后再提问；仅汇报进度时不要调用 ask_user。");
                return (new List<AskUserQuestion> { new AskUserQuestion { Id = "q1", Question = text } }, timeout);
            }
            if (single.ValueKind == JsonValueKind.Object)
                return (new List<AskUserQuestion> { ParseQuestion(single, 1) }, timeout);
        }
        if (root.TryGetProperty("questionsYaml", out var yEl) && yEl.ValueKind == JsonValueKind.String)
            return (MiniYamlParser.ParseQuestions(yEl.GetString() ?? string.Empty), timeout);

        throw new AskUserParseException("缺少 questions（1-4 题）。失败自愈模板：{\"questions\":[{\"id\":\"q1\",\"question\":\"你的问题\",\"options\":[]}]}");
    }

    internal static List<AskUserQuestion> ParseQuestionsArray(JsonElement arr, int baseIndex)
    {
        var list = new List<AskUserQuestion>();
        int i = baseIndex;
        foreach (var el in arr.EnumerateArray())
        {
            i++;
            if (el.ValueKind != JsonValueKind.Object)
                throw new AskUserParseException($"第 {i} 题必须是对象 {{id, question, options?}}。");
            list.Add(ParseQuestion(el, i));
        }
        return list;
    }

    internal static AskUserQuestion ParseQuestion(JsonElement el, int index)
    {
        string id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? (idEl.GetString() ?? string.Empty).Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(id)) id = "q" + index;
        string question = el.TryGetProperty("question", out var qEl) && qEl.ValueKind == JsonValueKind.String
            ? (qEl.GetString() ?? string.Empty).Trim() : string.Empty;
        var q = new AskUserQuestion { Id = id, Question = question };
        if (el.TryGetProperty("multiSelect", out var msEl) && msEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            q.MultiSelect = msEl.GetBoolean();
        if (el.TryGetProperty("allowCustom", out var acEl) && acEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            q.AllowCustom = acEl.GetBoolean();
        if (el.TryGetProperty("default", out var dEl) && dEl.ValueKind == JsonValueKind.String)
            q.Default = dEl.GetString();
        if (el.TryGetProperty("options", out var oEl) && oEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in oEl.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.String)
                {
                    string label = (o.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(label)) q.Options.Add(new AskUserOption { Label = label });
                }
                else if (o.ValueKind == JsonValueKind.Object)
                {
                    string label = o.TryGetProperty("label", out var lEl) && lEl.ValueKind == JsonValueKind.String
                        ? (lEl.GetString() ?? string.Empty).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(label)) continue;
                    string? desc = o.TryGetProperty("description", out var deEl) && deEl.ValueKind == JsonValueKind.String
                        ? deEl.GetString() : null;
                    q.Options.Add(new AskUserOption { Label = label, Description = desc });
                }
            }
        }
        return q;
    }

    public static string? ValidateQuestions(List<AskUserQuestion>? questions)
    {
        if (questions == null || questions.Count == 0)
            return "至少需要 1 道问题（最多 4 题）。自愈模板：{\"questions\":[{\"id\":\"q1\",\"question\":\"你的问题\"}]}";
        if (questions.Count > 4)
            return $"一次最多提 4 道问题，本次 {questions.Count} 题已拒绝。请精简为 4 题以内后重试（前端不会弹窗，无残留）。";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (string.IsNullOrWhiteSpace(q.Question))
                return $"第 {i + 1} 题缺少 question 文本，请补充后重试。";
            if (!seen.Add(q.Id))
                return $"问题 id 重复：'{q.Id}'，请保证每题 id 唯一。";
            if (q.Options.Count > 6)
                return $"问题 '{q.Id}' 最多 6 个选项，本次 {q.Options.Count} 个已拒绝，请精简后重试。";
        }
        return null;
    }

    public static string BuildOutput(List<AskUserQuestion> questions, string answersJson)
    {
        var answers = new List<(string Id, List<string> Selected, string Custom, bool Skipped)>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(answersJson) ? "{}" : answersJson);
            JsonElement root = doc.RootElement;
            JsonElement arr = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("answers", out var aEl))
                arr = aEl;
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    string id = a.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? (idEl.GetString() ?? string.Empty) : string.Empty;
                    var sel = new List<string>();
                    if (a.TryGetProperty("selected", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
                        foreach (var s in sEl.EnumerateArray())
                            if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                                sel.Add(s.GetString()!);
                    string custom = a.TryGetProperty("customInput", out var cEl) && cEl.ValueKind == JsonValueKind.String ? (cEl.GetString() ?? string.Empty).Trim() : string.Empty;
                    if (custom.Length > CustomMaxChars) custom = custom.Substring(0, CustomMaxChars);
                    bool skipped = a.TryGetProperty("skipped", out var skEl) && skEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? skEl.GetBoolean() : (sel.Count == 0 && string.IsNullOrEmpty(custom));
                    answers.Add((id, sel, custom, skipped));
                }
            }
        }
        catch { /* 答案解析失败则摘要兜底 */ }

        int answered = answers.Count(a => !a.Skipped);
        var sb = new StringBuilder();
        sb.Append($"用户已回答（{answered}/{questions.Count}）：");
        foreach (var q in questions)
        {
            var hit = answers.FirstOrDefault(a => string.Equals(a.Id, q.Id, StringComparison.OrdinalIgnoreCase));
            sb.Append($"[{q.Id}] ");
            if (hit.Id == null && answers.Count > 0) { sb.Append("未作答；"); continue; }
            if (hit.Skipped || (hit.Selected.Count == 0 && string.IsNullOrEmpty(hit.Custom))) sb.Append("跳过；");
            else
            {
                if (hit.Selected.Count > 0) sb.Append(string.Join("、", hit.Selected) + "；");
                if (!string.IsNullOrEmpty(hit.Custom)) sb.Append("自定义：" + hit.Custom + "；");
            }
        }
        string summary = sb.ToString();
        if (summary.Length > SummaryMaxChars) summary = summary.Substring(0, SummaryMaxChars);

        string pretty = answersJson ?? "{}";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(pretty) ? "{}" : pretty);
            pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { /* 保持原文 */ }
        return summary + "\n```json\n" + pretty + "\n```";
    }
}

public sealed class AskUserParseException : Exception
{
    public AskUserParseException(string message) : base(message) { }
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
            if (map.TryGetValue("questions", out var q)) node = q;
            else if (map.TryGetValue("question", out var single)) node = new List<object?> { single };
        }
        if (node is List<object?> list)
        {
            var out2 = new List<AskUserQuestion>();
            int i = 0;
            foreach (var item in list)
            {
                i++;
                if (item is Dictionary<string, object?> m) out2.Add(ToQuestion(m, i));
                else throw new AskUserParseException($"YAML 第 {i} 题必须是映射（id/question/options）。自愈模板：{{\"questions\":[{{\"id\":\"q1\",\"question\":\"你的问题\"}}]}}");
            }
            if (out2.Count == 0) throw new AskUserParseException("YAML 中未找到 questions 列表。");
            return out2;
        }
        throw new AskUserParseException("YAML 解析失败或结构不符（需要 questions: 列表）。自愈模板：{\"questions\":[{\"id\":\"q1\",\"question\":\"你的问题\"}]}");
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
            if (string.IsNullOrWhiteSpace(line)) continue;
            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
            string text = line.Substring(indent);
            if (text.StartsWith("#")) continue;
            text = StripTrailingComment(text);
            if (string.IsNullOrWhiteSpace(text)) continue;
            result.Add((indent, text.TrimEnd()));
        }
        return result;
    }

    private static string StripTrailingComment(string text)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && i > 0 && char.IsWhiteSpace(text[i - 1]))
                return text.Substring(0, i).TrimEnd();
        }
        return text;
    }

    private static Dictionary<string, object?> ParseMap(List<(int Indent, string Text)> lines, ref int pos, int parentIndent)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        while (pos < lines.Count && lines[pos].Indent > parentIndent)
        {
            var (indent, text) = lines[pos];
            if (text.StartsWith("- "))
                break;
            int colon = FindKeyColon(text);
            if (colon < 0) { pos++; continue; }
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
                else map[key] = string.Empty;
            }
        }
        return map;
    }

    private static List<object?> ParseList(List<(int Indent, string Text)> lines, ref int pos, int parentIndent)
    {
        var list = new List<object?>();
        while (pos < lines.Count && lines[pos].Indent > parentIndent && lines[pos].Text.StartsWith("- "))
        {
            var (indent, text) = lines[pos];
            string item = text.Substring(2).Trim();
            pos++;
            if (item.Length == 0)
            {
                if (pos < lines.Count && lines[pos].Indent > indent)
                {
                    if (lines[pos].Text.StartsWith("- ")) list.Add(ParseList(lines, ref pos, indent));
                    else list.Add(ParseMap(lines, ref pos, indent));
                }
                else list.Add(string.Empty);
            }
            else
            {
                int colon = FindKeyColon(item);
                if (colon >= 0)
                {
                    var m = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    m[item.Substring(0, colon).Trim().Trim('"', '\'')] = Unquote(item.Substring(colon + 1).Trim());
                    if (pos < lines.Count && lines[pos].Indent > indent && !lines[pos].Text.StartsWith("- "))
                    {
                        var cont = ParseMap(lines, ref pos, indent);
                        foreach (var kv in cont) m[kv.Key] = kv.Value;
                    }
                    list.Add(m);
                }
                else list.Add(Unquote(item));
            }
        }
        return list;
    }

    private static string ReadLiteralBlock(List<(int Indent, string Text)> lines, ref int pos, int parentIndent)
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
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ':' && !inSingle && !inDouble && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || text[i + 1] == '#'))
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
        string id = m.TryGetValue("id", out var idO) ? (idO?.ToString() ?? string.Empty).Trim() : string.Empty;
        if (string.IsNullOrEmpty(id)) id = "q" + index;
        string question = m.TryGetValue("question", out var qO) ? (qO?.ToString() ?? string.Empty).Trim() : string.Empty;
        var q = new AskUserQuestion { Id = id, Question = question };
        if (m.TryGetValue("multiSelect", out var msO)) q.MultiSelect = IsTrue(msO);
        if (m.TryGetValue("allowCustom", out var acO)) q.AllowCustom = IsTrue(acO);
        if (m.TryGetValue("default", out var dO)) q.Default = dO?.ToString();
        if (m.TryGetValue("options", out var oO) && oO is List<object?> opts)
        {
            foreach (var o in opts)
            {
                if (o is string s && !string.IsNullOrWhiteSpace(s)) q.Options.Add(new AskUserOption { Label = s.Trim() });
                else if (o is Dictionary<string, object?> om)
                {
                    string label = om.TryGetValue("label", out var lO) ? (lO?.ToString() ?? string.Empty).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(label)) continue;
                    q.Options.Add(new AskUserOption
                    {
                        Label = label,
                        Description = om.TryGetValue("description", out var deO) ? deO?.ToString() : null,
                    });
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
