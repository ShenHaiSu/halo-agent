namespace HaloAgent.Bridge.Contracts;

/// <summary>
/// need21-B01: 桥返回信封 canonical 版（B02 起统一用此；Host.Desktop.Core.Models.BridgeResponse 逐步迁移到此）。
/// JSON 小驼峰不变，前后端无感。
/// </summary>
public sealed class BridgeResponse<T>
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public T? Data { get; init; }

    public static BridgeResponse<T> Success(T data, string? message = null) =>
        new()
        {
            Ok = true,
            Data = data,
            Message = message
        };

    public static BridgeResponse<T> Fail(string code, string message) =>
        new()
        {
            Ok = false,
            Code = code,
            Message = message
        };
}
