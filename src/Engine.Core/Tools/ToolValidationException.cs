namespace HaloAgent.Engine.Core.Tools;

/// <summary>单条参数错误：点号路径 + 期望 + 实际。</summary>
public sealed record ToolValidationError(string Path, string Message);

/// <summary>
/// 参数校验失败。Message 已是「可直接回灌给模型的完整文案」，
/// 调用方只管 catch 后当 tool result 用，不要再加前缀。
/// </summary>
public sealed class ToolValidationException : Exception
{
    public IReadOnlyList<ToolValidationError> Errors { get; }

    public ToolValidationException(string message, IReadOnlyList<ToolValidationError> errors)
        : base(message)
    {
        Errors = errors;
    }
}
