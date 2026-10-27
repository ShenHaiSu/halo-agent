using System.Text.Json;

namespace HaloAgent.Engine.Abstractions.Serialization;

/// <summary>
/// 全项目唯一的 JSON 配置。任何地方要序列化/反序列化通信载荷都走这里，
/// 禁止各写各的 options——那会导致"有的地方字段名 camelCase、有的是 PascalCase"。
/// </summary>
public static class HaloJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // 中文不转 \uXXXX：日志与调试可读性远重要于几个字节
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>反序列化单个帧（宽容：未知字段忽略；坏行返回 null）。</summary>
    public static object? ReadFrame(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        // 防御：.NET 的 StreamWriter(Encoding.UTF8) 默认带 BOM 前导，
        // 若发送方忘了用 new UTF8Encoding(false)，这里容忍前导 BOM。
        line = line.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tEl)) return null;
            return tEl.GetString() switch
            {
                "req" => new FrameDto.Request(
                    root.GetProperty("id").GetInt64(),
                    root.TryGetProperty("m", out var m) ? m.GetString() ?? "" : "",
                    root.TryGetProperty("p", out var p) ? p.Clone() : (JsonElement?)null),
                "evt" => new FrameDto.Event(
                    root.TryGetProperty("seq", out var s) ? s.GetInt64() : 0,
                    root.TryGetProperty("k", out var k) ? k.GetString() ?? "" : "",
                    root.TryGetProperty("p", out var pp) ? pp.Clone() : (JsonElement?)null),
                "res" => new FrameDto.Response(
                    root.GetProperty("id").GetInt64(),
                    root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                    root.TryGetProperty("r", out var r) ? r.Clone() : (JsonElement?)null,
                    root.TryGetProperty("e", out var e) ? e.Clone() : (JsonElement?)null),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // 坏行不炸：返回 null，由调用方决定记日志还是丢弃
            return null;
        }
    }
}

/// <summary>读出来的原始帧（不直接反序列化成 record，便于宽容处理未知字段）。</summary>
public static class FrameDto
{
    public sealed record Request(long Id, string Method, JsonElement? Params);
    public sealed record Response(long Id, bool Ok, JsonElement? Result, JsonElement? Error);
    public sealed record Event(long Seq, string Kind, JsonElement? Payload);
}
