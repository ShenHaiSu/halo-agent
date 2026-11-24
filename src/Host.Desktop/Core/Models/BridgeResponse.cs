using System.Text.Json.Serialization;

namespace Host.Desktop.Core.Models;

/// <summary>
/// 统一 JS Bridge 异步返回信封，避免 C# 异常直接抛向 JS 导致堆栈丢失。
/// </summary>
public sealed class BridgeResponse<T>
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public T? Data { get; init; }

    /// <summary>回滚失败时残留的会话 ID，供前端展示与手动清理。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewSessionId { get; init; }

    /// <summary>失败阶段（如回滚的数据库分区、目录或索引）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailedStage { get; init; }

    public static BridgeResponse<T> Success(T data, string? message = null) =>
        new()
        {
            Ok = true,
            Data = data,
            Message = message
        };

    public static BridgeResponse<T> Fail(
        string code,
        string message,
        string? newSessionId = null,
        string? failedStage = null
    ) =>
        new()
        {
            Ok = false,
            Code = code,
            Message = message,
            NewSessionId = newSessionId,
            FailedStage = failedStage
        };
}
