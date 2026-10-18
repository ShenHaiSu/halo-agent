using System.Diagnostics;
using System.Text.Json;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need15 P2-4：统一 edit 入口（可选，已实现）。
/// mode=text 走 EditFileTool 引擎；mode=lines 走 EditLinesTool 引擎。老 edit_file/edit_lines 保留。
/// </summary>
public sealed class EditTool : IToolHandler
{
    private readonly EditFileTool _text = new();
    private readonly EditLinesTool _lines = new();

    public string Name => "edit";

    public string Description =>
        "Unified entry; prefer this over edit_file/edit_lines for new code. " +
        "mode=text edits by string anchor; mode=lines edits by 1-based line range. " +
        "Example text: {\"file_path\":\"src/a.ts\",\"mode\":\"text\",\"old_string\":\"...\",\"new_string\":\"...\"} " +
        "Example lines: {\"file_path\":\"src/a.ts\",\"mode\":\"lines\",\"offset\":10,\"limit\":3,\"new_text\":\"...\"}";

    public ToolDefinition GetDefinition() => new(Name, Description,
        "{\"type\":\"object\",\"properties\":{"
        + "\"file_path\":{\"type\":\"string\",\"description\":\"Target file path.\"},"
        + "\"mode\":{\"type\":\"string\",\"enum\":[\"text\",\"lines\"],\"description\":\"text=string anchor (edit_file engine); lines=line range (edit_lines engine).\"},"
        + "\"old_string\":{\"type\":\"string\",\"description\":\"Required for mode=text.\"},"
        + "\"new_string\":{\"type\":\"string\",\"description\":\"Required for mode=text.\"},"
        + "\"replace_all\":{\"type\":\"boolean\",\"description\":\"Only for mode=text.\"},"
        + "\"match_mode\":{\"type\":\"string\",\"enum\":[\"exact\",\"normalized\",\"regex\"],\"description\":\"Only for mode=text.\"},"
        + "\"offset\":{\"type\":\"integer\",\"description\":\"Required for mode=lines (1-based).\"},"
        + "\"limit\":{\"type\":\"integer\",\"description\":\"Required for mode=lines (0=insert).\"},"
        + "\"new_text\":{\"type\":\"string\",\"description\":\"Required for mode=lines.\"},"
        + "\"dry_run\":{\"type\":\"boolean\",\"description\":\"Preview only.\"},"
        + "\"expected_hash\":{\"type\":\"string\",\"description\":\"Optional concurrency guard.\"}"
        + "},\"required\":[\"file_path\",\"mode\"]}");

    public Task<ToolResult> ExecuteAsync(ToolCallDto call, ToolContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string mode = "text";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("mode", out JsonElement m) && m.ValueKind == JsonValueKind.String)
            {
                mode = (m.GetString() ?? "text").Trim().ToLowerInvariant();
            }
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ToolResult.Fail(
                $"参数 JSON 解析失败：{ex.Message}。",
                durationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            sw.Stop();
        }

        if (mode == "lines")
        {
            // 将 mode 剥离后转调 edit_lines（其余参数名与 edit_lines 一致）
            string forwarded = ForwardWithoutMode(call.ArgumentsJson);
            return _lines.ExecuteAsync(new ToolCallDto(call.Id, "edit_lines", forwarded), context, ct);
        }
        else if (mode == "text")
        {
            string forwarded = ForwardWithoutMode(call.ArgumentsJson);
            return _text.ExecuteAsync(new ToolCallDto(call.Id, "edit_file", forwarded), context, ct);
        }
        else
        {
            return Task.FromResult(ToolResult.Fail(
                $"mode 取值非法：'{mode}'。可选值：text | lines。"));
        }
    }

    private static string ForwardWithoutMode(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            using var ms = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "mode", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    prop.WriteTo(w);
                }

                w.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return argumentsJson;
        }
    }
}
