namespace HaloAgent.Engine.Abstractions.Frames;

/// <summary>
/// 错误码常量。与 contract/agent.schema.json 的 errorCodes 一一对应。
/// 用常量而不是枚举：序列化简单，且前端拿到的是字符串。
/// </summary>
public static class RpcError
{
    /// <summary>方法不存在。</summary>
    public const string MethodNotFound = "method.notFound";
    /// <summary>参数不合法。</summary>
    public const string ParamsInvalid = "params.invalid";
    /// <summary>协议版本不匹配。</summary>
    public const string ProtocolVersion = "protocol.version";
    /// <summary>有回合正在执行。</summary>
    public const string StateBusy = "state.busy";
    /// <summary>尚未配置模型。</summary>
    public const string StateNotConfigured = "state.notConfigured";
    /// <summary>会话不存在。</summary>
    public const string SessionNotFound = "session.notFound";
    /// <summary>技能/人格/记忆不存在。</summary>
    public const string AssetNotFound = "asset.notFound";
    /// <summary>路径不存在。</summary>
    public const string FsNotFound = "fs.notFound";
    /// <summary>路径越界。</summary>
    public const string FsEscape = "fs.escape";
    /// <summary>模型端点错误。</summary>
    public const string LlmError = "llm.error";
    /// <summary>模型超时。</summary>
    public const string LlmTimeout = "llm.timeout";
    /// <summary>工具执行失败。</summary>
    public const string ToolFailed = "tool.failed";
    /// <summary>回答了一个已过期的询问。</summary>
    public const string AskStale = "ask.stale";
    /// <summary>引擎内部错误。</summary>
    public const string Internal = "internal";
    /// <summary>该功能未实现。</summary>
    public const string NotSupported = "notSupported";

    /// <summary>全部错误码（供 system.protocol 报告与测试校验）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        MethodNotFound, ParamsInvalid, ProtocolVersion, StateBusy, StateNotConfigured,
        SessionNotFound, AssetNotFound, FsNotFound, FsEscape, LlmError, LlmTimeout,
        ToolFailed, AskStale, Internal, NotSupported,
    };
}

/// <summary>
/// 可抛出的 RPC 异常。被 FrameServer 捕获后转成 res 帧的 e 字段。
/// Hint 是“人读建议”，前端可以直接展示。
/// </summary>
public sealed class RpcException : Exception
{
    /// <summary>错误码。</summary>
    public string Code { get; }
    /// <summary>人读建议（可为空）。</summary>
    public string? Hint { get; }

    /// <summary>创建 RPC 异常。</summary>
    public RpcException(string code, string message, string? hint = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Hint = hint;
    }

    /// <summary>未知方法。</summary>
    public static RpcException MethodNotFound(string method) =>
        new(RpcError.MethodNotFound, $"未知方法: {method}",
            "检查帧的 m 字段拼写；用 system.protocol 查看全部方法");

    /// <summary>参数不合法。</summary>
    public static RpcException ParamsInvalid(string detail) =>
        new(RpcError.ParamsInvalid, $"参数不合法: {detail}");

    /// <summary>有回合正在执行。</summary>
    public static RpcException Busy(string detail = "有回合正在执行") =>
        new(RpcError.StateBusy, detail, "先中断（Esc）或等它结束");

    /// <summary>尚未配置模型。</summary>
    public static RpcException NotConfigured() =>
        new(RpcError.StateNotConfigured, "尚未配置模型",
            "在设置 › 服务商与模型中完成配置");
}
