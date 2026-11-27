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

/// <summary>need21-B02: 工作区/会话。</summary>
public sealed partial class HostBridgeFacade
{
    public async Task<BridgeResponse<List<WorkspaceInfo>>> GetWorkspaces()
    {
        try
        {
            var list = await _workspaceSessionService.GetAllWorkspacesAsync().ConfigureAwait(false);
            return BridgeResponse<List<WorkspaceInfo>>.Success(list);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetWorkspaces failed", ex);
            return BridgeResponse<List<WorkspaceInfo>>.Fail(ex.Message, "WORKSPACE_ERROR");
        }
    }

    public async Task<BridgeResponse<WorkspaceInfo>> AddWorkspace(
        string rootPath,
        string? name = null
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return BridgeResponse<WorkspaceInfo>.Fail("工作路径不能为空", "INVALID_PATH");
            }
            var ws = await _workspaceSessionService
                .AddOrUpdateWorkspaceAsync(rootPath, name)
                .ConfigureAwait(false);
            return BridgeResponse<WorkspaceInfo>.Success(ws);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"AddWorkspace failed for path '{rootPath}'", ex);
            return BridgeResponse<WorkspaceInfo>.Fail(ex.Message, "WORKSPACE_ERROR");
        }
    }

    /// <summary>设置工作区别名（纯前端视觉）。alias 传空串/null = 清除别名。</summary>
    public async Task<BridgeResponse<WorkspaceInfo>> RenameWorkspace(
        string workspaceId,
        string? alias
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return BridgeResponse<WorkspaceInfo>.Fail("INVALID_WORKSPACE_ID", "工作区 ID 不能为空");
            }
            var ws = await _workspaceSessionService
                .SetWorkspaceAliasAsync(workspaceId, alias)
                .ConfigureAwait(false);
            if (ws == null)
            {
                return BridgeResponse<WorkspaceInfo>.Fail(
                    "WORKSPACE_NOT_FOUND",
                    $"工作区不存在：'{workspaceId}'"
                );
            }
            return BridgeResponse<WorkspaceInfo>.Success(ws);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"RenameWorkspace failed for id '{workspaceId}'", ex);
            return BridgeResponse<WorkspaceInfo>.Fail(ex.Message, "WORKSPACE_ERROR");
        }
    }

    /// <summary>显式双模式入口：mode 只接受 unbind / purge；旧方法不会静默获得 Purge 能力。</summary>
    public async Task<BridgeResponse<WorkspaceRemovalResultDto>> RemoveWorkspaceV2(
        string workspaceId,
        string mode
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BridgeResponse<WorkspaceRemovalResultDto>.Fail(
                "INVALID_WORKSPACE_ID",
                "工作区 ID 不能为空"
            );
        if (!WorkspaceRemovalModeParser.TryParse(mode, out var parsedMode))
            return BridgeResponse<WorkspaceRemovalResultDto>.Fail(
                "WORKSPACE_VALIDATION",
                "移除模式只接受 unbind 或 purge"
            );

        try
        {
            Action<string>? cancelTree =
                _subCoordinator == null ? null : id => _subCoordinator.CancelTree(id);
            var result = await _workspaceSessionService
                .RemoveWorkspaceV2Async(workspaceId.Trim(), parsedMode, default, cancelTree)
                .ConfigureAwait(false);
            return BridgeResponse<WorkspaceRemovalResultDto>.Success(
                WorkspaceRemovalResultDto.FromResult(result),
                result.Outcome switch
                {
                    WorkspaceRemovalOutcome.Completed
                        => parsedMode == WorkspaceRemovalMode.Unbind ? "已移出列表；物理清理完成" : "工作区已彻底删除",
                    WorkspaceRemovalOutcome.CompletedWithWarnings => "已移出列表，但部分物理数据清理失败或被保守保留",
                    WorkspaceRemovalOutcome.NotFound => "工作区不存在",
                    _ => WorkspaceRemovalErrorMessage(result.ErrorCode),
                }
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"RemoveWorkspaceV2 failed for id '{workspaceId}' mode '{mode}'",
                ex
            );
            return BridgeResponse<WorkspaceRemovalResultDto>.Fail(
                "WORKSPACE_REMOVAL_ERROR",
                WorkspaceRemovalErrorMessage("WORKSPACE_REMOVAL_ERROR")
            );
        }
    }

    /// <summary>旧入口保持“仅移出列表”的文案语义：新宿主上永远安全解绑，不再默认彻底删除。</summary>
    public async Task<BridgeResponse<bool>> RemoveWorkspace(string workspaceId)
    {
        var response = await RemoveWorkspaceV2(workspaceId, "unbind").ConfigureAwait(false);
        if (!response.Ok)
        {
            return BridgeResponse<bool>.Fail(
                response.Code ?? "WORKSPACE_REMOVAL_ERROR",
                response.Message ?? WorkspaceRemovalErrorMessage(response.Code)
            );
        }
        return BridgeResponse<bool>.Success(
            response.Data?.WorkspaceUnbound == true,
            response.Message
        );
    }

    private static string WorkspaceRemovalErrorMessage(string? code) =>
        code switch
        {
            "INVALID_WORKSPACE_ID" => "工作区 ID 不能为空或不合法",
            "WORKSPACE_VALIDATION" => "移除模式只接受 unbind 或 purge",
            "WORKSPACE_BUSY" => "工作区正在执行其他操作，请稍后重试",
            "CANCEL_FAILED" => "无法确认会话已停止，未执行删除",
            "REPARSE_POINT_UNSUPPORTED" => "检测到链接目录，出于安全原因未执行删除",
            "PATH_TRAVERSAL" => "工作区路径越界，未执行删除",
            "SESSIONS_INDEX_INVALID" => "会话索引损坏，未执行删除",
            "WORKSPACE_NOT_FOUND" => "工作区不存在",
            "DELETE_CANCELLED" => "移除已取消",
            _ => "工作区移除失败，请查看日志后重试",
        };

    public async Task<BridgeResponse<string?>> BrowseWorkspaceFolder(string? initialPath = null)
    {
        try
        {
            var selectedPath = await InvokeOnUiThreadAsync(() =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "选择项目工作路径 (Workspace Root Folder)",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    SelectedPath = Directory.Exists(initialPath) ? initialPath : string.Empty
                };

                var result = dialog.ShowDialog(_mainForm);
                return result == DialogResult.OK ? dialog.SelectedPath : null;
            });

            return BridgeResponse<string?>.Success(selectedPath);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("BrowseWorkspaceFolder failed", ex);
            return BridgeResponse<string?>.Fail(ex.Message, "DIALOG_ERROR");
        }
    }

    public async Task<BridgeResponse<List<SessionMetadata>>> GetSessions(string workspaceId)
    {
        try
        {
            var list = await _workspaceSessionService
                .GetSessionsByWorkspaceAsync(workspaceId)
                .ConfigureAwait(false);
            return BridgeResponse<List<SessionMetadata>>.Success(list);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GetSessions failed for workspace '{workspaceId}'", ex);
            return BridgeResponse<List<SessionMetadata>>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<SessionMetadata>> CreateSession(
        string workspaceId,
        string? title = null,
        string? providerId = null,
        string? modelId = null
    )
    {
        try
        {
            var meta = await _workspaceSessionService
                .CreateSessionAsync(workspaceId, title, providerId, modelId)
                .ConfigureAwait(false);
            return BridgeResponse<SessionMetadata>.Success(meta);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"CreateSession failed in workspace '{workspaceId}'", ex);
            return BridgeResponse<SessionMetadata>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    /// <summary>旧全量消息直读入口已随统一仓储切换删除，请使用时间轴摘要接口。</summary>

    /// <summary>标题链专用：按回合取指定回合的用户全文，摘要层直读，与消息门无关。</summary>
    public async Task<BridgeResponse<string>> GetTurnUserContent(
        string workspaceId,
        string sessionId,
        string turnId
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return BridgeResponse<string>.Fail("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空");
            }

            var store = new WorkspaceSessionStore(_storageManager, _sessionRepository);
            string content;
            try
            {
                content = await store
                    .GetUserContentAsync(workspaceId, sessionId, turnId ?? string.Empty)
                    .ConfigureAwait(false);
            }
            catch (LegacySessionFormatException legacy)
            {
                return BridgeResponse<string>.Fail("LEGACY_SESSION", legacy.Message);
            }
            catch (UnsupportedFormatException unsupported)
            {
                return BridgeResponse<string>.Fail("UNSUPPORTED_FORMAT", unsupported.Message);
            }

            return BridgeResponse<string>.Success(content ?? string.Empty);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GetTurnUserContent failed for session '{sessionId}'", ex);
            return BridgeResponse<string>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<bool>> AppendSessionMessage(
        string workspaceId,
        string sessionId,
        string messageJson
    )
    {
        try
        {
            try
            {
                var meta = await _workspaceSessionService
                    .GetSessionAsync(workspaceId, sessionId)
                    .ConfigureAwait(false);
                if (
                    meta != null
                    && string.Equals(meta.AgentRole, "sub", StringComparison.OrdinalIgnoreCase)
                )
                    return BridgeResponse<bool>.Fail("SUB_READONLY", "子会话历史只读");
            }
            catch { }
            var msg = JsonSerializer.Deserialize<UnifiedChatMessage>(
                messageJson,
                HaloJsonOptions.IndentedCamelCase
            );
            if (msg == null)
            {
                return BridgeResponse<bool>.Fail("消息 JSON 无法反序列化", "INVALID_MESSAGE");
            }
            bool ok = await _workspaceSessionService
                .AppendSessionMessageAsync(workspaceId, sessionId, msg)
                .ConfigureAwait(false);
            return BridgeResponse<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"AppendSessionMessage failed for session '{sessionId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<bool>> UpdateSessionTitle(
        string workspaceId,
        string sessionId,
        string title
    )
    {
        try
        {
            bool ok = await _workspaceSessionService
                .UpdateSessionTitleAsync(workspaceId, sessionId, title)
                .ConfigureAwait(false);
            return BridgeResponse<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"UpdateSessionTitle failed for session '{sessionId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<bool>> SetSessionPinned(
        string workspaceId,
        string sessionId,
        bool isPinned
    )
    {
        try
        {
            bool ok = await _workspaceSessionService
                .SetSessionPinnedAsync(workspaceId, sessionId, isPinned)
                .ConfigureAwait(false);
            return BridgeResponse<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"SetSessionPinned failed for session '{sessionId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<SessionDeletionResultDto>> DeleteSessionV2(
        string workspaceId,
        string sessionId
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
        {
            return BridgeResponse<SessionDeletionResultDto>.Fail(
                "INVALID_SESSION",
                "工作区 ID 与会话 ID 不能为空"
            );
        }

        try
        {
            Action<string>? cancelTree =
                _subCoordinator == null ? null : id => _subCoordinator.CancelTree(id);
            var result = await _workspaceSessionService
                .DeleteSessionTreeAsync(workspaceId.Trim(), sessionId.Trim(), default, cancelTree)
                .ConfigureAwait(false);
            return BridgeResponse<SessionDeletionResultDto>.Success(
                SessionDeletionResultDto.FromResult(result),
                result.Outcome switch
                {
                    SessionDeletionOutcome.Deleted => "会话已删除",
                    SessionDeletionOutcome.Partial => "会话数据已删除，但存在目录或索引残留",
                    SessionDeletionOutcome.NotFound => "会话不存在",
                    _ => DeletionErrorMessage(result.ErrorCode),
                }
            );
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"DeleteSessionV2 failed for workspace '{workspaceId}' session '{sessionId}'",
                ex
            );
            return BridgeResponse<SessionDeletionResultDto>.Fail(
                "SESSION_DELETE_ERROR",
                DeletionErrorMessage("SESSION_DELETE_ERROR")
            );
        }
    }

    public async Task<BridgeResponse<bool>> DeleteSession(string workspaceId, string sessionId)
    {
        var response = await DeleteSessionV2(workspaceId, sessionId).ConfigureAwait(false);
        if (!response.Ok)
        {
            return BridgeResponse<bool>.Fail(
                response.Code ?? "SESSION_DELETE_ERROR",
                response.Message ?? DeletionErrorMessage(response.Code)
            );
        }

        return BridgeResponse<bool>.Success(response.Data?.Outcome == "deleted", response.Message);
    }

    private static string DeletionErrorMessage(string? code) =>
        code switch
        {
            "SESSION_NOT_FOUND" => "会话不存在，可能已被删除",
            "SESSION_BUSY" => "会话正在生成、压缩或执行历史操作，请稍后重试",
            "HISTORY_OPERATION_IN_PROGRESS" => "会话正在执行其他历史操作，请稍后重试",
            "WORKSPACE_BUSY" => "工作区正在执行其他操作，请稍后重试",
            "BACKUP_FAILED" => "备份失败，会话未删除",
            "CANCEL_FAILED" => "无法确认会话已停止，会话未删除",
            "SESSION_TREE_CYCLE" => "会话树结构异常，已拒绝删除",
            "SESSION_DATABASE_DELETE_FAILED" => "数据库删除失败，会话未删除",
            "DELETE_CANCELLED" => "删除已取消",
            "INVALID_SESSION" => "工作区 ID 与会话 ID 不能为空",
            _ => "会话删除失败，请查看日志后重试",
        };

    public async Task<BridgeResponse<string>> ExportSessionMarkdown(
        string workspaceId,
        string sessionId
    )
    {
        try
        {
            string md = await _workspaceSessionService
                .ExportSessionMarkdownAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            return BridgeResponse<string>.Success(md);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ExportSessionMarkdown failed for session '{sessionId}'", ex);
            return BridgeResponse<string>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<ContextBudgetStatusDto>> GetContextBudget(
        string providerId,
        string modelId,
        string workspaceId,
        string sessionId
    )
    {
        try
        {
            var status = await _tokenBudgetService
                .GetBudgetStatusAsync(providerId, modelId, workspaceId, sessionId)
                .ConfigureAwait(false);
            return BridgeResponse<ContextBudgetStatusDto>.Success(status);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetContextBudget failed", ex);
            return BridgeResponse<ContextBudgetStatusDto>.Fail(ex.Message, "BUDGET_ERROR");
        }
    }
}
