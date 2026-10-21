using System.Text;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Tools.Safety;
using Host.Desktop.Tools.Validation;

namespace Host.Desktop.Tools;

/// <summary>
/// need21-B04: 工具管线（Validation → Safety → Handler，顺序写死、可单测）。
/// ToolRegistry 只做路由（查 handler + 透传），本类承载校验/安全/执行/守卫/日志。
/// </summary>
public sealed class ToolPipeline
{
    private static readonly string[] PathKeys = new[]
    {
        "file_path",
        "path",
        "dir",
        "directory",
        "target_path",
        "file",
    };

    private static readonly HashSet<string> FileTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "read_file",
            "write_file",
            "edit_file",
            "edit_lines",
            "edit",
            "list_dir",
            "grep_search",
            "undo_edit",
            "view_image",
        };

    /// <summary>Validation 中间件：中央校验（失败不进执行，不计时）。</summary>
    public static ToolCallValidationResult Validate(
        string toolName,
        string? argumentsJson,
        bool closedWorld
    ) => ToolCallValidator.Validate(toolName, argumentsJson, closedWorld);

    /// <summary>
    /// Safety 中间件：文件类工具的路径键集中越界拦截。
    /// 非文件工具/无路径键直接放行（各工具内联检查保持为纵深防御）。
    /// </summary>
    public static (bool Allowed, string? Code, string? Message) CheckSafety(
        string toolName,
        string normalizedJson,
        string workspaceRoot
    )
    {
        if (!FileTools.Contains(toolName))
            return (true, null, null);
        string[] keys;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(normalizedJson) ? "{}" : normalizedJson
            );
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (true, null, null);
            var found = new List<string>();
            foreach (var key in PathKeys)
            {
                if (
                    root.TryGetProperty(key, out var prop)
                    && prop.ValueKind == System.Text.Json.JsonValueKind.String
                )
                {
                    string? v = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                        found.Add(v);
                }
            }
            if (found.Count == 0)
                return (true, null, null);
            bool isWrite =
                toolName.Equals("write_file", StringComparison.OrdinalIgnoreCase)
                || toolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase)
                || toolName.Equals("edit_lines", StringComparison.OrdinalIgnoreCase)
                || toolName.Equals("edit", StringComparison.OrdinalIgnoreCase)
                || toolName.Equals("undo_edit", StringComparison.OrdinalIgnoreCase);
            foreach (var raw in found)
            {
                var (allowed, error, _, _) = isWrite
                    ? PathSafetyValidator.ValidateWrite(workspaceRoot, raw)
                    : PathSafetyValidator.ValidateRead(workspaceRoot, raw);
                if (!allowed)
                {
                    string code = (error ?? string.Empty).Contains(
                        PathSafetyValidator.Codes.OutsideWorkspace
                    )
                        ? PathSafetyValidator.Codes.OutsideWorkspace
                        : "PATH_DENIED";
                    return (false, code, error ?? "路径安全校验失败。");
                }
            }
            return (true, null, null);
        }
        catch
        {
            return (true, null, null);
        }
    }

    /// <summary>
    /// 管线执行：路由（resolver 查 handler/未知→UNKNOWN_TOOL；Disabled/激活集→TOOL_NOT_ENABLED）
    /// → Validation → Safety → Handler → 输出守卫 → warnings → 计时日志。
    /// 与旧 ToolRegistry.DispatchAsync 行为一致（纯搬迁 + Safety 顺序固化）。
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(
        ToolCallDto call,
        ToolContext context,
        ISet<string>? activeTools,
        CancellationToken ct,
        bool closedWorld,
        Func<string, IToolHandler?> resolver,
        Func<string> availableNames
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string toolName = call?.Name?.Trim() ?? string.Empty;
        string callId = string.IsNullOrWhiteSpace(call?.Id)
            ? Guid.NewGuid().ToString("N")
            : call.Id;

        IToolHandler? handler = resolver(toolName);
        if (handler == null)
        {
            sw.Stop();
            return ToolResult.FailCode(
                "UNKNOWN_TOOL",
                $"未知工具 '{toolName}'。可用工具：{availableNames()}。请修正工具名后重试。",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { tool = toolName }),
                durationMs: sw.ElapsedMilliseconds
            );
        }

        ToolExposure exposure = ToolExposure.Direct;
        try
        {
            exposure = handler.GetDefinition()?.Exposure ?? ToolExposure.Direct;
        }
        catch (Exception)
        {
            exposure = ToolExposure.Direct;
        }
        if (exposure == ToolExposure.Disabled)
        {
            sw.Stop();
            return ToolResult.FailCode(
                "TOOL_NOT_ENABLED",
                $"工具 '{toolName}' 已被禁用（Exposure=Disabled）。请改用其他工具后重试。",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { tool = toolName }),
                durationMs: sw.ElapsedMilliseconds
            );
        }

        if (
            activeTools != null
            && !activeTools.Any(n => string.Equals(n, toolName, StringComparison.OrdinalIgnoreCase))
        )
        {
            sw.Stop();
            string available = string.Join(
                "、",
                activeTools.OrderBy(n => n, StringComparer.Ordinal)
            );
            return ToolResult.FailCode(
                "TOOL_NOT_ENABLED",
                $"工具 '{toolName}' 存在但未在本轮启用。本轮可用工具：{available}。请切换 toolsPreset 或在 toolsAllowlist 中点名该工具后重试。",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { tool = toolName }),
                durationMs: sw.ElapsedMilliseconds
            );
        }

        var validation = Validate(toolName, call?.ArgumentsJson, closedWorld);
        if (!validation.Success)
        {
            sw.Stop();
            return ToolResult.FailCode(
                validation.Code ?? "INVALID_JSON_ARGUMENTS",
                validation.Message ?? "参数校验失败。",
                payloadJson: validation.PayloadJson,
                durationMs: sw.ElapsedMilliseconds
            );
        }

        ToolCallDto execCall = call!;
        if (
            !string.Equals(validation.NormalizedJson, call?.ArgumentsJson, StringComparison.Ordinal)
        )
        {
            execCall = new ToolCallDto(call!.Id, call!.Name, validation.NormalizedJson);
        }

        var safety = CheckSafety(
            toolName,
            validation.NormalizedJson ?? "{}",
            context.WorkspaceRoot
        );
        if (!safety.Allowed)
        {
            sw.Stop();
            return ToolResult.FailCode(
                safety.Code ?? "PATH_DENIED",
                safety.Message ?? "路径安全校验失败。",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { tool = toolName }),
                durationMs: sw.ElapsedMilliseconds
            );
        }

        ToolResult result;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                context.CancellationToken
            );
            result = await handler
                .ExecuteAsync(execCall, context, linkedCts.Token)
                .ConfigureAwait(false);
            result ??= ToolResult.Fail($"工具 '{toolName}' 返回空结果，请重试。");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ToolResult.Fail($"工具 '{toolName}' 执行已被取消。", durationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            HaloLogger.Tool.Error($"Tool '{toolName}' (call {callId}) threw: {ex.Message}", ex);
            return ToolResult.Fail(
                $"工具 '{toolName}' 执行异常：{ex.Message}。请修正参数后重试；若持续失败，请改用其他工具或缩小操作范围。",
                durationMs: sw.ElapsedMilliseconds
            );
        }

        try
        {
            if (!string.IsNullOrEmpty(result.Output))
            {
                var (display, spilled) = ToolOutputGuard.ApplyGuard(
                    result.Output,
                    context.DataRoot,
                    context.WorkspaceId,
                    context.SessionId,
                    callId
                );
                result.Output = display;
                if (!string.IsNullOrEmpty(spilled))
                {
                    result.SpilledFile ??= spilled;
                }
            }
        }
        catch (Exception) { }

        if (validation.Warnings.Count > 0)
        {
            result.ValidatedWarnings = new List<string>(validation.Warnings);
            string tail = string.Join("\n", validation.Warnings.Select(w => "Warning: " + w));
            if (result.Success)
            {
                result.Output = string.IsNullOrEmpty(result.Output)
                    ? tail
                    : result.Output + "\n" + tail;
            }
            else
            {
                result.Error = string.IsNullOrEmpty(result.Error)
                    ? tail
                    : result.Error + "\n" + tail;
            }
        }

        if (result.DurationMs <= 0)
        {
            result.DurationMs = sw.ElapsedMilliseconds;
        }

        sw.Stop();
        try
        {
            HaloLogger.Tool.Info(
                $"Tool '{toolName}' (call {callId}) finished: success={result.Success} exit={result.ExitCode} cost={result.DurationMs}ms"
                    + (result.SpilledFile != null ? $" spilled={result.SpilledFile}" : string.Empty)
            );
        }
        catch (Exception) { }

        return result;
    }
}
