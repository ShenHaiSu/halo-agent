using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using CefSharp;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.LlmGateway.Models;
using Host.Desktop.Services;
using Host.Desktop.Tools;

namespace Host.Desktop.Bridge;

/// <summary>need21-B02: 工具/压缩。</summary>
public sealed partial class HostBridgeFacade
{
    private ToolRegistry ToolRegistryInternal2 => _toolRegistry ??= new ToolRegistry();

    /// <summary>
    /// Stage 05 前端调试：导出工具定义 Schema（protocol: chat | resp）。
    /// </summary>
    public Task<BridgeResponse<string>> GetToolDefinitions(string protocol = "chat")
    {
        try
        {
            string normalized = (protocol ?? "chat").Trim().ToLowerInvariant();
            string json =
                normalized == "resp"
                    ? ToolRegistryInternal2.GetResponsesToolsJson()
                    : ToolRegistryInternal2.GetChatToolsJson();
            return Task.FromResult(BridgeResponse<string>.Success(json));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetToolDefinitions failed", ex);
            return Task.FromResult(BridgeResponse<string>.Fail("TOOLS_SCHEMA_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Stage 05 前端调试：执行单个工具（复用 ToolRegistry，不经过 LLM）。
    /// </summary>
    public async Task<BridgeResponse<ToolResult>> ExecuteTool(
        string workspaceId,
        string sessionId,
        string toolName,
        string argumentsJson,
        string? toolCallId = null
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return BridgeResponse<ToolResult>.Fail("INVALID_TOOL", "工具名不能为空");
            }

            var ws = await _workspaceSessionService
                .GetWorkspaceAsync(workspaceId)
                .ConfigureAwait(false);
            if (ws == null)
            {
                return BridgeResponse<ToolResult>.Fail(
                    "WORKSPACE_NOT_FOUND",
                    $"工作区不存在：'{workspaceId}'"
                );
            }
            var context = new ToolContext
            {
                WorkspaceRoot = ws.RootPath,
                WorkspaceId = workspaceId,
                SessionId = sessionId ?? string.Empty,
                DataRoot = _storageManager.CurrentDataRoot,
            };
            try
            {
                var agentCfg = _agentConfigService.GetInternal();
                context.ExecDefaultTimeoutMsOverride = agentCfg.ExecDefaultTimeoutMs;
                context.ExecMaxTimeoutMsOverride = agentCfg.ExecMaxTimeoutMs;
                context.AllowedCommandsOverride = agentCfg.AllowedCommands;
            }
            catch
            { /* 调试单跑快照失败则回退常量，不阻断 */
            }
            var call = new ToolCallDto(
                string.IsNullOrWhiteSpace(toolCallId)
                    ? $"call_{Guid.NewGuid():N}"[..12]
                    : toolCallId.Trim(),
                toolName.Trim(),
                argumentsJson
            );
            ToolResult result = await ToolRegistryInternal2
                .DispatchAsync(call, context)
                .ConfigureAwait(false);
            return BridgeResponse<ToolResult>.Success(result);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ExecuteTool failed for '{toolName}'", ex);
            return BridgeResponse<ToolResult>.Fail("TOOL_EXEC_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// need05-03：/compact 入口。后台 Task 运行压缩，不阻塞 CEF 线程；
    /// 前后发布 agent:turn-event（CompactStarted → CompactCompleted/CompactFailed）。
    /// 不复用 _activeSessionTokens 的 agent: 键（避免与推理互相取消），另起 compact: 键且不提供 Cancel 入口。
    /// </summary>
    public async Task<BridgeResponse<CompactionResult>> CompactSession(
        string workspaceId,
        string sessionId,
        string? providerId = null,
        string? modelId = null,
        int keepLastGroups = 6
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return BridgeResponse<CompactionResult>.Fail(
                    "INVALID_SESSION",
                    "工作区 ID 与会话 ID 不能为空"
                );
            }

            string wsId = workspaceId.Trim();
            string sessId = sessionId.Trim();
            try
            {
                var meta = await _workspaceSessionService
                    .GetSessionAsync(wsId, sessId)
                    .ConfigureAwait(false);
                if (
                    meta != null
                    && string.Equals(meta.AgentRole, "sub", StringComparison.OrdinalIgnoreCase)
                )
                    return BridgeResponse<CompactionResult>.Fail("SUB_READONLY", "子会话只读，不可压缩");
            }
            catch { }
            string? provId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
            string? modId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
            int keep = keepLastGroups < 1 ? 6 : keepLastGroups;
            string compactKey = "compact:" + sessId;

            if (_activeSessionTokens.ContainsKey(compactKey))
            {
                return BridgeResponse<CompactionResult>.Fail("COMPACT_BUSY", "压缩进行中，请稍候");
            }

            JsEventDispatcher.Publish(
                "agent:turn-event",
                new
                {
                    sessionId = sessId,
                    kind = "CompactStarted",
                    payload = new { sessionId = sessId },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            );

            // need22-P1 T5：CTS 同步前置注册（消除检查与后台注册之间的竞态；CancelCompaction 可靠中断）。
            var compactCts = new CancellationTokenSource();
            _activeSessionTokens[compactKey] = compactCts;
            _ = Task.Run(
                async () =>
                {
                    var cts = compactCts;
                    try
                    {
                        var result = await CompactionService
                            .CompactAsync(wsId, sessId, provId, modId, keep, cts.Token)
                            .ConfigureAwait(false);
                        if (result.Success)
                        {
                            // P007：占用基线取压缩后即时重算的实值（通常为 compact-estimate），禁止写死。
                            string occupancyBaseline = "compact-estimate";
                            try
                            {
                                var active = await _workspaceSessionService
                                    .GetActiveContextMessagesAsync(
                                        wsId,
                                        sessId,
                                        CancellationToken.None
                                    )
                                    .ConfigureAwait(false);
                                var stub = await _workspaceSessionService
                                    .GetLastCompactStubAsync(wsId, sessId, CancellationToken.None)
                                    .ConfigureAwait(false);
                                var probe = new ModelConfig
                                {
                                    Id = "compact-baseline",
                                    MaxContextTokens = 128000,
                                    MaxOutputTokens = 4096
                                };
                                occupancyBaseline =
                                    _tokenBudgetService
                                        .CalculateBudgetStatus(probe, active, null, 0, stub)
                                        .OccupancyBaseline ?? "compact-estimate";
                            }
                            catch
                            {
                                // 预算重算失败不阻断压缩完成事件，按暂存态回退
                            }
                            JsEventDispatcher.Publish(
                                "agent:turn-event",
                                new
                                {
                                    sessionId = sessId,
                                    kind = "CompactCompleted",
                                    payload = new
                                    {
                                        removedMessages = result.RemovedMessages,
                                        keptMessages = result.KeptMessages,
                                        summaryPreview = result.SummaryPreview,
                                        backupFileName = result.BackupFileName,
                                        stubMessageId = result.StubMessageId,
                                        summaryTokens = result.SummaryOutputTokens,
                                        occupancyBaseline,
                                        updatedAtUtc = DateTime.UtcNow.ToString("o")
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }
                            );
                        }
                        else
                        {
                            JsEventDispatcher.Publish(
                                "agent:turn-event",
                                new
                                {
                                    sessionId = sessId,
                                    kind = "CompactFailed",
                                    payload = new
                                    {
                                        errorCode = result.ErrorCode,
                                        errorMessage = result.ErrorMessage
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        HaloLogger.App.Error(
                            $"[HostBridge] Compact failed for session '{sessId}'",
                            ex
                        );
                        JsEventDispatcher.Publish(
                            "agent:turn-event",
                            new
                            {
                                sessionId = sessId,
                                kind = "CompactFailed",
                                payload = new
                                {
                                    errorCode = "COMPACT_EXCEPTION",
                                    errorMessage = ex.Message
                                },
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            }
                        );
                    }
                    finally
                    {
                        _activeSessionTokens.TryRemove(compactKey, out _);
                        cts.Dispose();
                    }
                },
                CancellationToken.None
            );

            return BridgeResponse<CompactionResult>.Success(default!, "压缩已启动");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("CompactSession failed", ex);
            return BridgeResponse<CompactionResult>.Fail("COMPACT_START_ERROR", ex.Message);
        }
    }

    /// <summary>列出工作区库快照备份（审计用），返回 JSON 数组字符串。</summary>
    public Task<BridgeResponse<string>> GetCompactBackupList(string workspaceId, string sessionId)
    {
        try
        {
            var names = new List<string>();
            string backupPath = _storageManager.GetWorkspaceBackupPath(workspaceId);
            if (File.Exists(backupPath))
            {
                names.Add(Path.GetFileName(backupPath));
            }
            string json = JsonSerializer.Serialize(names, HaloJsonOptions.IndentedCamelCase);
            return Task.FromResult(BridgeResponse<string>.Success(json));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GetCompactBackupList failed for session '{sessionId}'", ex);
            return Task.FromResult(
                BridgeResponse<string>.Fail("COMPACT_BACKUP_LIST_ERROR", ex.Message)
            );
        }
    }
}
