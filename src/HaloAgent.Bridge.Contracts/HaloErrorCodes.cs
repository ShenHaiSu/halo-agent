namespace HaloAgent.Bridge.Contracts;

/// <summary>need21-B01: 桥错误码 canonical 版（与前端 shared/bridge/protocol 对齐）。</summary>
public static class HaloErrorCodes
{
    public const string Ok = "OK";
    public const string InvalidArgs = "INVALID_ARGS";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Internal = "INTERNAL";
    public const string Cancelled = "CANCELLED";
    public const string Truncated = "TRUNCATED";
    public const string PathDenied = "PATH_DENIED";
    public const string ToolDenied = "TOOL_DENIED";
    public const string ValidationFailed = "VALIDATION_FAILED";
}
