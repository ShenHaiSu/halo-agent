using System.Buffers;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Serialization;

namespace HaloAgent.Engine.Abstractions.Frames;

/// <summary>
/// NDJSON 编解码器。全项目唯一的“Frame ↔ UTF-8 bytes”转换点。
/// 关键设计：
///   1. 编码时不申请字符串：直接写到 IBufferWriter&lt;byte&gt;。
///      这是为了高频小帧（流式文本）时避免每次分配一个新的 string。
///   2. 解码时宽容：非法 JSON / 未知 t 值 → 返回 null（调用方记日志），
///      不抛异常。一条坏帧不应该让整条流断掉。
///   3. 单帧上限 16 MB（可配置）：超限直接失败，防止内存炸弹。
///   4. 行尾固定 \n（不写 \r\n）：跨平台一致，测试简单。
/// </summary>
public static class FrameCodec
{
    /// <summary>默认单帧上限：16 MB。</summary>
    public const int DefaultMaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>把一帧写进 buffer（含行尾 \n）。</summary>
    public static void Write(IBufferWriter<byte> writer, Frame frame, JsonSerializerOptions? opts = null)
    {
        // 注意：Utf8JsonWriter 必须在写完 Dispose 才会 flush 到 writer
        using (var w = new Utf8JsonWriter(writer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            switch (frame)
            {
                case RequestFrame req:
                    w.WriteString("t", "req");
                    w.WriteNumber("id", req.Id);
                    w.WriteString("m", req.Method);
                    if (req.Params.HasValue)
                    {
                        w.WritePropertyName("p");
                        req.Params.Value.WriteTo(w);
                    }
                    break;

                case ResponseFrame res:
                    w.WriteString("t", "res");
                    w.WriteNumber("id", res.Id);
                    w.WriteBoolean("ok", res.Ok);
                    if (res.Ok && res.Result.HasValue)
                    {
                        w.WritePropertyName("r");
                        res.Result.Value.WriteTo(w);
                    }
                    else if (!res.Ok && res.Error is not null)
                    {
                        w.WritePropertyName("e");
                        JsonSerializer.Serialize(w, res.Error, opts ?? HaloJson.Options);
                    }
                    break;

                case EventFrame ev:
                    w.WriteString("t", "evt");
                    w.WriteNumber("seq", ev.Seq);
                    w.WriteString("k", ev.Kind);
                    if (ev.Payload.HasValue)
                    {
                        w.WritePropertyName("p");
                        ev.Payload.Value.WriteTo(w);
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(frame),
                        $"未知帧类型: {frame.GetType().Name}");
            }
            w.WriteEndObject();
        }

        // 行尾
        var tail = writer.GetSpan(1);
        tail[0] = (byte)'\n';
        writer.Advance(1);
    }

    /// <summary>把一帧写成 byte[]（测试/日志用；生产路径走 IBufferWriter）。</summary>
    public static byte[] ToBytes(Frame frame)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        Write(buf, frame);
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>解析一行（不含行尾 \n）。返回 null 表示不是合法帧。</summary>
    public static Frame? Parse(ReadOnlySpan<byte> line, out string? parseError)
    {
        parseError = null;
        if (line.IsEmpty)
        {
            parseError = "空行";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                parseError = "根不是对象";
                return null;
            }
            if (!root.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String)
            {
                parseError = "缺少 t 字段";
                return null;
            }

            return tEl.GetString() switch
            {
                "req" => ParseRequest(root, out parseError),
                "res" => ParseResponse(root, out parseError),
                "evt" => ParseEvent(root, out parseError),
                var other => Fail($"未知帧类型 t={other}", out parseError),
            };
        }
        catch (JsonException ex)
        {
            parseError = $"JSON 解析失败: {ex.Message}";
            return null;
        }
    }

    private static Frame? Fail(string reason, out string? err)
    {
        err = reason;
        return null;
    }

    private static Frame? ParseRequest(JsonElement root, out string? err)
    {
        err = null;
        if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
            return Fail("req 缺少有效 id", out err);
        if (!root.TryGetProperty("m", out var mEl) || mEl.ValueKind != JsonValueKind.String)
            return Fail("req 缺少 m", out err);

        JsonElement? p = root.TryGetProperty("p", out var pEl) && pEl.ValueKind is not JsonValueKind.Null
            ? pEl.Clone()
            : null;

        return new RequestFrame { Id = id, Method = mEl.GetString()!, Params = p };
    }

    private static Frame? ParseResponse(JsonElement root, out string? err)
    {
        err = null;
        if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
            return Fail("res 缺少有效 id", out err);
        if (!root.TryGetProperty("ok", out var okEl) ||
            okEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Fail("res 缺少 ok", out err);

        bool ok = okEl.GetBoolean();
        JsonElement? r = root.TryGetProperty("r", out var rEl) && rEl.ValueKind != JsonValueKind.Null
            ? rEl.Clone()
            : null;

        ErrorPayload? e = null;
        if (!ok && root.TryGetProperty("e", out var eEl) && eEl.ValueKind == JsonValueKind.Object)
        {
            e = new ErrorPayload
            {
                Code = eEl.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                Msg = eEl.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "",
                Hint = eEl.TryGetProperty("hint", out var h) ? h.GetString() : null,
            };
        }

        return new ResponseFrame { Id = id, Ok = ok, Result = r, Error = e };
    }

    private static Frame? ParseEvent(JsonElement root, out string? err)
    {
        err = null;
        if (!root.TryGetProperty("seq", out var seqEl) || !seqEl.TryGetInt64(out var seq))
            return Fail("evt 缺少有效 seq", out err);
        if (!root.TryGetProperty("k", out var kEl) || kEl.ValueKind != JsonValueKind.String)
            return Fail("evt 缺少 k", out err);

        JsonElement? p = root.TryGetProperty("p", out var pEl) && pEl.ValueKind != JsonValueKind.Null
            ? pEl.Clone()
            : null;

        return new EventFrame { Seq = seq, Kind = kEl.GetString()!, Payload = p };
    }
}
