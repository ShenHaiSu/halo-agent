using System.Text.Json;
using HaloAgent.Engine.Core.Tools;

namespace HaloAgent.Engine.Core.Llm;

/// <summary>chat → responses 翻译（MessageAdapters 保持 Chat↔Stored 专用，不扩职责）。</summary>
public static class ResponsesTranslators
{
    public static List<ResponsesToolDef> TranslateToolDefs(IReadOnlyList<object> chatDefs)
    {
        var out_ = new List<ResponsesToolDef>();
        foreach (var d in chatDefs)
        {
            try
            {
                var json = JsonSerializer.Serialize(d);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string name = "", desc = "";
                object? parameters = null;
                if (root.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString() ?? "";
                    if (fn.TryGetProperty("description", out var dd) && dd.ValueKind == JsonValueKind.String) desc = dd.GetString() ?? "";
                    if (fn.TryGetProperty("parameters", out var p)) parameters = JsonSerializer.Deserialize<object>(p.GetRawText());
                }
                else
                {
                    if (root.TryGetProperty("name", out var n2) && n2.ValueKind == JsonValueKind.String) name = n2.GetString() ?? "";
                }
                if (string.IsNullOrEmpty(name)) continue;
                out_.Add(new ResponsesToolDef { Type = "function", Name = name, Description = desc, Parameters = parameters });
            }
            catch { continue; }
        }
        return out_;
    }

    public static List<ResponsesToolDef> TranslateToolDefsFromRegistry(ToolRegistry registry)
    {
        var defs = registry.ToToolDefs();
        return TranslateToolDefs(defs);
    }

    public static (string? instructions, List<object> input) TranslateMessages(IReadOnlyList<ChatMessage> messages)
    {
        string? instructions = null;
        var input = new List<object>();
        foreach (var m in messages)
        {
            if (m.Role == Role.System)
            {
                var text = m.Content ?? "";
                if (m.ContentParts is { Count: > 0 })
                    text = string.Join("\n", m.ContentParts.Where(p => p.Type == "text").Select(p => p.Text ?? ""));
                if (instructions is null) instructions = text;
                else instructions += "\n" + text;
                continue;
            }
            if (m.Role == Role.User)
            {
                input.Add(ToMessageItem("user", m));
                continue;
            }
            if (m.Role == Role.Assistant)
            {
                if (!string.IsNullOrEmpty(m.Content))
                    input.Add(new Dictionary<string, object?> { ["type"] = "message", ["role"] = "assistant", ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "output_text", ["text"] = m.Content } } });
                if (m.ToolCalls is { Count: > 0 })
                {
                    foreach (var tc in m.ToolCalls)
                    {
                        input.Add(new Dictionary<string, object?> { ["type"] = "function_call", ["call_id"] = tc.Id, ["name"] = tc.Function.Name, ["arguments"] = tc.Function.Arguments });
                    }
                }
                continue;
            }
            if (m.Role == Role.Tool)
            {
                input.Add(new Dictionary<string, object?> { ["type"] = "function_call_output", ["call_id"] = m.ToolCallId ?? "", ["output"] = m.Content ?? "" });
                continue;
            }
        }
        return (instructions, input);
    }

    private static object ToMessageItem(string role, ChatMessage m)
    {
        if (m.ContentParts is { Count: > 0 })
        {
            var content = new List<object>();
            foreach (var p in m.ContentParts)
            {
                if (p.Type == "image_url" && p.ImageUrl is not null)
                    content.Add(new Dictionary<string, object?> { ["type"] = "input_image", ["image_url"] = p.ImageUrl.Url });
                else
                    content.Add(new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = p.Text ?? "" });
            }
            return new Dictionary<string, object?> { ["type"] = "message", ["role"] = role, ["content"] = content };
        }
        return new Dictionary<string, object?> { ["type"] = "message", ["role"] = role, ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = m.Content ?? "" } } };
    }

    public static List<object> TranslateIncrementalOutputs(IReadOnlyList<ChatMessage> newMessages)
    {
        var (_, input) = TranslateMessages(newMessages);
        return input;
    }

    /// <summary>读回时拆包未知 Args（03 §3.3）：{rawType,payload,args} 包裹往返。</summary>
    public static (string kind, string? rawType, string args) UnpackUnknownArgs(string storedArgs)
    {
        try
        {
            using var doc = JsonDocument.Parse(storedArgs);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("rawType", out var rt) &&
                rt.ValueKind == JsonValueKind.String)
            {
                string? args = null;
                if (doc.RootElement.TryGetProperty("args", out var a))
                    args = a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText();
                return ("unknown", rt.GetString(), args ?? "{}");
            }
        }
        catch { }
        return ("function", null, storedArgs);
    }

    public static string PackUnknownArgs(string rawType, string rawJson, string argsFallback = "{}")
    {
        return JsonSerializer.Serialize(new { rawType, payload = rawJson, args = argsFallback });
    }
}
