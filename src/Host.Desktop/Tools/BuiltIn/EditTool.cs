using System.Diagnostics;
using System.Text.Json;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools.BuiltIn;

/// <summary>
/// need15 P2-4：统一 edit 入口（可选，已实现）。
/// mode=text 走 EditFileTool 引擎；mode=lines 走 EditLinesTool 引擎。老 edit_file/edit_lines 保留。
/// need17-01：Exposure=Hidden，可执行但不声明；Description 首行 DEPRECATED。
/// </summary>
public sealed class EditTool : IToolHandler
{
    private readonly EditFileTool _text = new();
    private readonly EditLinesTool _lines = new();

    public string Name => "edit";

    public string Description =>
        "DEPRECATED: prefer edit_file/edit_lines. "
        + "Unified entry; prefer this over edit_file/edit_lines for new code. "
        + "mode=text edits by string anchor; mode=lines edits by 1-based line range. "
        + "Example text: {\"file_path\":\"src/a.ts\",\"mode\":\"text\",\"old_string\":\"...\",\"new_string\":\"...\"} "
        + "Example lines: {\"file_path\":\"src/a.ts\",\"mode\":\"lines\",\"offset\":10,\"limit\":3,\"new_text\":\"...\"}";

    public ToolDefinition GetDefinition()
    {
        var def = new ToolDefinition(
            Name,
            Description,
            new ToolSchemaBuilder()
                .AddString("file_path", "Target file path.", required: true)
                .AddString(
                    "mode",
                    "text=string anchor (edit_file engine); lines=line range (edit_lines engine).",
                    required: true,
                    enumValues: new[] { "text", "lines" }
                )
                .AddString("old_string", "Required for mode=text.")
                .AddString("new_string", "Required for mode=text.")
                .AddBoolean("replace_all", "Only for mode=text.")
                .AddString(
                    "match_mode",
                    "Only for mode=text.",
                    enumValues: new[] { "exact", "normalized", "regex" }
                )
                .AddInteger("offset", "Required for mode=lines (1-based).")
                .AddInteger("limit", "Required for mode=lines (0=insert).")
                .AddString("new_text", "Required for mode=lines.")
                .AddBoolean("dry_run", "Preview only.")
                .AddString("expected_hash", "Optional concurrency guard.")
                .Build()
        );
        def.Exposure = ToolExposure.Hidden;
        return def;
    }

    public Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        CancellationToken ct = default
    )
    {
        var sw = Stopwatch.StartNew();
        string mode = "text";
        try
        {
            // need22-P0 S3：NormalizedJson 单次解析（ToolArgs 统一入口）
            using var args = ToolArgs.Parse(call.ArgumentsJson);
            string? m = args.Root.ValueKind == JsonValueKind.Object ? args.GetString("mode") : null;
            if (!string.IsNullOrWhiteSpace(m))
            {
                mode = m.Trim().ToLowerInvariant();
            }
        }
        catch (JsonException ex)
        {
            return Task.FromResult(
                ToolResult.Fail($"参数 JSON 解析失败：{ex.Message}。", durationMs: sw.ElapsedMilliseconds)
            );
        }
        finally
        {
            sw.Stop();
        }

        if (mode == "lines")
        {
            // 将 mode 剥离后转调 edit_lines（其余参数名与 edit_lines 一致）
            string forwarded = ForwardWithoutMode(call.ArgumentsJson);
            return _lines.ExecuteAsync(
                new ToolCallDto(call.Id, "edit_lines", forwarded),
                context,
                ct
            );
        }
        else if (mode == "text")
        {
            string forwarded = ForwardWithoutMode(call.ArgumentsJson);
            return _text.ExecuteAsync(
                new ToolCallDto(call.Id, "edit_file", forwarded),
                context,
                ct
            );
        }
        else
        {
            return Task.FromResult(ToolResult.Fail($"mode 取值非法：'{mode}'。可选值：text | lines。"));
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
