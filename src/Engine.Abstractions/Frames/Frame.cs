using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Abstractions.Frames;

/// <summary>
/// NDJSON 帧的三种形态。线上格式（每行一个 JSON 对象，\n 结尾）：
///   请求   {"t":"req","id":42,"m":"agent.prompt","p":{...}}
///   应答   {"t":"res","id":42,"ok":true,"r":{...}}
///   应答   {"t":"res","id":42,"ok":false,"e":{"code":"...","msg":"..."}}
///   事件   {"t":"evt","seq":10031,"k":"text.delta","p":{...}}
/// 设计取舍：
/// - 用 "t"/"m"/"p"/"k" 单字母字段名，因为帧头每 token 都要过网络/管道，
///   高频场景（流式文本 30-120 帧/秒）下体积差异真实存在。
/// - seq 单调递增，前端检测丢帧后触发 resync。
/// - 全部 camelCase，与前端 TS 习惯一致。
/// 线上字段说明：
/// - C# 侧的鉴别器叫 Type，但**不直接序列化**（JsonIgnore），
///   每个子类用 T 属性输出单字母 "t"（JsonPropertyName("t")）。
///   若把鉴别器直接标 [JsonPropertyName("t")]，子类 override 会同时产出
///   "type" 与 "t" 两个字段，违反 schema 的 additionalProperties:false。
/// </summary>
public abstract record Frame
{
    [JsonIgnore] public abstract string Type { get; }
}

/// <summary>宿主 → 引擎的请求。</summary>
public sealed record RequestFrame : Frame
{
    [JsonPropertyName("t")] public string T => Type;
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("m")] public string Method { get; init; } = "";
    [JsonPropertyName("p")] public JsonElement? Params { get; init; }

    [JsonIgnore] public override string Type => "req";
}

/// <summary>引擎 → 宿主的应答（成功或失败）。</summary>
public sealed record ResponseFrame : Frame
{
    [JsonPropertyName("t")] public string T => Type;
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("r")] public JsonElement? Result { get; init; }
    [JsonPropertyName("e")] public ErrorPayload? Error { get; init; }

    [JsonIgnore] public override string Type => "res";

    public static ResponseFrame Success(long id, object? result, JsonSerializerOptions opts) =>
        new()
        {
            Id = id,
            Ok = true,
            Result = result is null ? null : JsonSerializer.SerializeToElement(result, opts),
        };

    public static ResponseFrame Failure(long id, string code, string message) =>
        new()
        {
            Id = id,
            Ok = false,
            Error = new ErrorPayload { Code = code, Msg = message },
        };
}

/// <summary>引擎 → 宿主的单向事件（无 id，不期待应答）。</summary>
public sealed record EventFrame : Frame
{
    [JsonPropertyName("t")] public string T => Type;
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("k")] public string Kind { get; init; } = "";
    [JsonPropertyName("p")] public JsonElement? Payload { get; init; }

    [JsonIgnore] public override string Type => "evt";
}

public sealed record ErrorPayload
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("msg")] public string Msg { get; init; } = "";
    /// <summary>可选的人读提示（前端可直接展示）</summary>
    [JsonPropertyName("hint")] public string? Hint { get; init; }
}
