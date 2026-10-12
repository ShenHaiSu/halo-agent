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

    public static BridgeResponse<T> Success(T data, string? message = null) =>
        new() { Ok = true, Data = data, Message = message };

    public static BridgeResponse<T> Fail(string code, string message) =>
        new() { Ok = false, Code = code, Message = message };
}
