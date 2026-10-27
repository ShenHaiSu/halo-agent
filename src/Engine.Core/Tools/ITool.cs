using System.Text.Json;
using HaloAgent.Engine.Core.Permissions;

namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 工具接口。每个工具必须是<b>幂等可重入</b>的（同一参数多次调用结果一致），
/// 除了写文件/跑命令这类天然有副作用的（由 undo 与权限体系兜底）。
/// </summary>
public interface ITool
{
    /// <summary>工具名（与契约 tools 列表逐字一致）</summary>
    string Name { get; }

    /// <summary>给模型看的描述（与契约逐字一致）</summary>
    string Description { get; }

    /// <summary>JSON Schema（与契约逐字一致）</summary>
    JsonElement ParameterSchema { get; }

    /// <summary>需要的权限类别</summary>
    PermissionChecker.Operation Operation { get; }

    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct);
}

/// <summary>工具执行结果。</summary>
public sealed record ToolResult(bool Ok, string Output)
{
    public static ToolResult Success(string output) => new(true, output);
    public static ToolResult Failure(string message) => new(false, message);
}

/// <summary>
/// 工具执行上下文。
///
/// 只暴露工具<b>需要的</b>能力，不暴露 Session 本身：
///   - 工具不该能改会话状态
///   - 工具不该能发任意事件（只能通过 Host 的受限接口）
/// </summary>
public sealed class ToolContext
{
    public string Cwd { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public IToolHost Host { get; init; } = null!;
    public CancellationToken Ct { get; init; }

    /// <summary>当前工具调用 id（事件关联用）</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>
/// 工具宿主能力（由 Session 实现）。
/// </summary>
public interface IToolHost
{
    /// <summary>写文件前的 undo 快照</summary>
    Task<UndoSnapshotResult> BackupAsync(string path, string action, CancellationToken ct);

    /// <summary>推送工具实时输出（run_shell_command 用）</summary>
    void PublishToolOutput(string toolCallId, string delta);

    /// <summary>推送静默预警（level 0/1/2）</summary>
    void PublishSilentAlert(string toolCallId, int level);

    /// <summary>当前生效的网络代理（web_fetch 用）</summary>
    string? ProxyUrl { get; }

    /// <summary>数据目录（记忆工具用）</summary>
    string DataDir { get; }
}

public sealed record UndoSnapshotResult(bool BackedUp, string? SnapshotPath, string? Error);
