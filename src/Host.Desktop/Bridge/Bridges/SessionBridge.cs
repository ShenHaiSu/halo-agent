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

    public async Task<BridgeResponse<bool>> RemoveWorkspace(string workspaceId)
    {
        try
        {
            bool removed = await _workspaceSessionService
                .RemoveWorkspaceAsync(workspaceId)
                .ConfigureAwait(false);
            return BridgeResponse<bool>.Success(removed);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"RemoveWorkspace failed for id '{workspaceId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "WORKSPACE_ERROR");
        }
    }

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

    public async Task<BridgeResponse<List<UnifiedChatMessage>>> GetSessionMessages(
        string workspaceId,
        string sessionId
    )
    {
        try
        {
            var messages = await _workspaceSessionService
                .GetSessionMessagesAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            return BridgeResponse<List<UnifiedChatMessage>>.Success(messages);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GetSessionMessages failed for session '{sessionId}'", ex);
            return BridgeResponse<List<UnifiedChatMessage>>.Fail(ex.Message, "SESSION_ERROR");
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

    public async Task<BridgeResponse<bool>> DeleteSession(string workspaceId, string sessionId)
    {
        try
        {
            // need13：删主先 CancelTree 再级联删树（含子盘）
            try
            {
                _subCoordinator?.CancelTree(sessionId.Trim());
            }
            catch { }
            bool ok = await _workspaceSessionService
                .DeleteSessionTreeAsync(workspaceId, sessionId)
                .ConfigureAwait(false);
            return BridgeResponse<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"DeleteSession failed for session '{sessionId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

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
