using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Host.Desktop.Core.Models;

/// <summary>
/// need06：提示词配置 JSON 宽容解析。
 /// 而 System.Text.Json 默认只认 ISO 8601，导致 Save 回传必炸。这是首个携带 DateTime 往返 JS Bridge 的 DTO，之前没暴露。
/// </summary>
public static class PromptJsonOptions
{
    public static readonly JsonSerializerOptions Lenient = CreateLenient();

    private static JsonSerializerOptions CreateLenient()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new LenientDateTimeConverter());
        return options;
    }

    public static PromptConfigDocument DeserializeConfigDocument(string json)
    {
        var doc = JsonSerializer.Deserialize<PromptConfigDocument>(json, Lenient);
        return doc ?? throw new JsonException("提示词配置 JSON 解析为空");
    }
}

/// <summary>宽容 DateTime：ISO / "/Date(ms)/" / Unix 秒或毫秒数字 / 空与 null（→ default，由校验层归一为 UtcNow）。真垃圾仍抛 JsonException。</summary>
public sealed class LenientDateTimeConverter : JsonConverter<DateTime>
{
    private static readonly Regex MsAjaxRegex = new(@"/Date\((\-?\d+)([+\-]\d{4})?\)/", RegexOptions.Compiled);

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return ParseString(reader.GetString());
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long epoch))
                {
                    try
                    {
                        // 10 位=秒，13 位=毫秒
                        return epoch >= 100_000_000_000L
                            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                            : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                    }
                    catch
                    {
                        return default;
                    }
                }
                if (reader.TryGetDouble(out double d))
                {
                    try { return DateTimeOffset.FromUnixTimeMilliseconds((long)d).UtcDateTime; }
                    catch { return default; }
                }
                return default;
             case JsonTokenType.Null:
             case JsonTokenType.True:
             case JsonTokenType.False:
                 return default;
             case JsonTokenType.StartObject:
             case JsonTokenType.StartArray:
                 // 防御：前端曾把 CefSharp Date 归一成 {} 发过来（已在 hostBridge.ts 根治）。
                 // 此处直接跳过整段当缺失处理，由 Save 归一为 UtcNow，避免整单保存被炸。
                 reader.Skip();
                 return default;
             default:
                 throw new JsonException($"Unsupported token {reader.TokenType} for DateTime.");
         }
     }

    private static DateTime ParseString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        s = s.Trim();
        var m = MsAjaxRegex.Match(s);
        if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
            catch { /* fall through */ }
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) return dt;
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dt)) return dt;
        throw new JsonException($"JSON value '{s}' could not be converted to System.DateTime.");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
