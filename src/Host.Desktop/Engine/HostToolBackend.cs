using HaloAgent.Engine;
using HaloAgent.Engine.Abstractions;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;

namespace Host.Desktop.Engine;

/// <summary>
/// Host-side dispatch seam for the engine tool station. It runs every
/// model-planned call through the shared <see cref="ToolRegistry"/> pipeline
/// (central validation, safety, handler, output guard) and maps the raw
/// outcome onto the engine backend shape. The engine keeps truncation, spill,
/// timeout isolation and contract mapping; this adapter never invents them.
/// </summary>
public sealed class HostToolBackend : IEngineToolBackend
{
    private readonly ToolRegistry _registry;
    private readonly IWorkspaceSessionService _workspaces;
    private readonly StoragePathManager _storage;
    private readonly AgentConfigService _agentConfig;

    public HostToolBackend(
        ToolRegistry registry,
        IWorkspaceSessionService workspaces,
        StoragePathManager storage,
        AgentConfigService agentConfig
    )
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _agentConfig = agentConfig ?? throw new ArgumentNullException(nameof(agentConfig));
        EnsureMainRoleTools();
    }

    public async Task<EngineBackendToolResult> InvokeAsync(
        string toolName,
        string argumentsJson,
        EngineToolBackendContext context,
        CancellationToken cancellationToken
    )
    {
        string name = (toolName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            return new EngineBackendToolResult(
                false,
                string.Empty,
                "Tool name is empty.",
                -1,
                0,
                "UNKNOWN_TOOL"
            );
        }

        WorkspaceInfo? ws;
        try
        {
            ws = await _workspaces
                .GetWorkspaceAsync(context.WorkspaceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EngineBackendToolResult(
                false,
                string.Empty,
                $"Workspace lookup failed: {Trim(ex.Message)}",
                -1,
                0,
                "WORKSPACE_NOT_FOUND"
            );
        }

        if (ws is null)
        {
            return new EngineBackendToolResult(
                false,
                string.Empty,
                $"Workspace is unknown: '{context.WorkspaceId}'.",
                -1,
                0,
                "WORKSPACE_NOT_FOUND"
            );
        }

        var toolContext = new ToolContext
        {
            WorkspaceRoot = ws.RootPath ?? string.Empty,
            WorkspaceId = context.WorkspaceId,
            SessionId = context.SessionId,
            DataRoot = _storage.CurrentDataRoot,
            CancellationToken = cancellationToken,
            AgentRole = context.AgentRole,
            ParentSessionId = context.ParentSessionId,
        };

        try
        {
            AgentConfigDocument cfg = _agentConfig.GetInternal();
            toolContext.ExecDefaultTimeoutMsOverride = cfg.ExecDefaultTimeoutMs;
            toolContext.ExecMaxTimeoutMsOverride = cfg.ExecMaxTimeoutMs;
            toolContext.AllowedCommandsOverride = cfg.AllowedCommands;
            toolContext.SubAgentEnabled = cfg.SubAgentEnabled;
            toolContext.SubMaxParallel = cfg.SubMaxParallel;
            toolContext.SubMaxIterations = cfg.SubMaxIterations;
            toolContext.SubAllowExecCommand = cfg.SubAllowExecCommand;
            toolContext.SubAllowFileWrite = cfg.SubAllowFileWrite;
            toolContext.SubAllowSkillWrite = cfg.SubAllowSkillWrite;
            toolContext.SubAllowMemoryWrite = cfg.SubAllowMemoryWrite;
        }
        catch
        {
            // Config snapshot failure falls back to pipeline constants; never blocks dispatch.
        }

        var call = new ToolCallDto(context.ToolCallId, name, argumentsJson);
        ToolResult result;
        try
        {
            result =
                await _registry
                    .DispatchAsync(call, toolContext, null, cancellationToken, context.ClosedWorld)
                    .ConfigureAwait(false) ?? ToolResult.Fail($"Tool '{name}' returned no result.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            HaloLogger.Tool.Error($"Engine tool '{name}' dispatch threw.", ex);
            return new EngineBackendToolResult(
                false,
                string.Empty,
                Trim(ex.Message),
                -1,
                0,
                "INTERNAL"
            );
        }

        return MapResult(result, _storage.CurrentDataRoot);
    }

    internal static EngineBackendToolResult MapResult(ToolResult result, string dataRoot)
    {
        string output = result.Output ?? string.Empty;
        string? error = result.Error;
        List<EngineImageReference>? images = null;
        if (result.Images is { Count: > 0 })
        {
            images = new List<EngineImageReference>(result.Images.Count);
            foreach (ChatImageRef image in result.Images)
            {
                images.Add(
                    new EngineImageReference(
                        image.FileName ?? string.Empty,
                        image.RelPath ?? string.Empty,
                        image.Mime ?? string.Empty,
                        image.Width,
                        image.Height,
                        image.SizeBytes,
                        image.IsMissing
                    )
                );
            }
        }

        List<string>? warnings = result.ValidatedWarnings is { Count: > 0 }
            ? new List<string>(result.ValidatedWarnings)
            : null;

        return new EngineBackendToolResult(
            result.Success,
            output,
            error,
            result.ExitCode,
            result.DurationMs,
            result.ErrorCode,
            result.ErrorPayloadJson,
            ToRelativeSpill(result.SpilledFile, dataRoot),
            result.FileBytes,
            images,
            warnings
        );
    }

    private static string? ToRelativeSpill(string? spilledFile, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(spilledFile))
        {
            return null;
        }
        try
        {
            string root = string.IsNullOrWhiteSpace(dataRoot) ? Path.GetTempPath() : dataRoot;
            string relative = Path.GetRelativePath(root, spilledFile.Trim());
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return Path.GetFileName(spilledFile.Trim())?.Replace('\\', '/');
            }
            return relative.Replace('\\', '/');
        }
        catch
        {
            return spilledFile.Trim().Replace('\\', '/');
        }
    }

    private void EnsureMainRoleTools()
    {
        try
        {
            if (!_registry.ToolNames.Contains("spawn_subagent", StringComparer.OrdinalIgnoreCase))
            {
                _registry.Register(new SpawnSubAgentTool(enabled: true));
            }
        }
        catch (Exception ex)
        {
            HaloLogger.Tool.Error("Engine tool backend could not register spawn_subagent.", ex);
        }
    }

    private static string Trim(string? hint)
    {
        if (string.IsNullOrEmpty(hint))
        {
            return string.Empty;
        }
        return hint.Length > 300 ? hint.Substring(0, 300) : hint;
    }
}
