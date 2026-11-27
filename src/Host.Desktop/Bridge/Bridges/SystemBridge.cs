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

/// <summary>need21-B02: 系统信息/存储配置。</summary>
public sealed partial class HostBridgeFacade
{
    public Task<BridgeResponse<SystemRuntimeInfo>> GetSystemInfo()
    {
        try
        {
            var osVer = Environment.OSVersion.Version;
            var isWin7 = osVer.Major == 6 && osVer.Minor == 1;
            var cefVersion = typeof(Cef).Assembly.GetName().Version?.ToString() ?? "109.1.110";

            var info = new SystemRuntimeInfo
            {
                OsDescription = RuntimeInformation.OSDescription,
                OsVersionString = Environment.OSVersion.VersionString,
                IsWin7 = isWin7,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                DotnetVersion = RuntimeInformation.FrameworkDescription,
                CefSharpVersion = cefVersion,
                CurrentDataRoot = _storageManager.CurrentDataRoot,
                BootstrapFilePath = _storageManager.BootstrapFilePath,
                LogsDirectory = _storageManager.LogsDirectory,
                GpuAccelerationDisabled = _gpuDisabled,
                TlsProtocols = ServicePointManager.SecurityProtocol.ToString()
            };

            return Task.FromResult(BridgeResponse<SystemRuntimeInfo>.Success(info));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetSystemInfo failed", ex);
            return Task.FromResult(
                BridgeResponse<SystemRuntimeInfo>.Fail("SYS_INFO_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<StorageConfigInfo>> GetStorageConfig()
    {
        try
        {
            var config = _storageManager.GetStorageConfigInfo();
            return Task.FromResult(BridgeResponse<StorageConfigInfo>.Success(config));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetStorageConfig failed", ex);
            return Task.FromResult(
                BridgeResponse<StorageConfigInfo>.Fail("STORAGE_CONFIG_ERROR", ex.Message)
            );
        }
    }

    public Task<BridgeResponse<StorageBreakdown>> GetStorageBreakdown()
    {
        try
        {
            var breakdown = _storageManager.GetStorageBreakdown();
            return Task.FromResult(BridgeResponse<StorageBreakdown>.Success(breakdown));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetStorageBreakdown failed", ex);
            return Task.FromResult(
                BridgeResponse<StorageBreakdown>.Fail("STORAGE_BREAKDOWN_ERROR", ex.Message)
            );
        }
    }

    /// <summary>
    /// 手动压实工作区历史库：忙时拒绝，不破坏原库与旧备份；压实前 UI 应提示先备份。
    /// </summary>
    public async Task<BridgeResponse<WorkspaceCompactionResult>> CompactWorkspaceHistory(
        string workspaceId
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BridgeResponse<WorkspaceCompactionResult>.Fail(
                "INVALID_WORKSPACE_ID",
                "工作区 ID 不能为空"
            );

        try
        {
            var result = await StorageMaintenance
                .CompactWorkspaceHistoryAsync(workspaceId.Trim())
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return BridgeResponse<WorkspaceCompactionResult>.Fail(
                    result.ErrorCode ?? "COMPACTION_FAILED",
                    CompactionErrorMessage(result.ErrorCode)
                );
            }

            return BridgeResponse<WorkspaceCompactionResult>.Success(
                result,
                result.ReclaimedBytes > 0
                    ? $"历史库压实完成，已回收 {result.ReclaimedBytes} 字节"
                    : "历史库压实完成，无可回收空间"
            );
        }
        catch (OperationCanceledException)
        {
            return BridgeResponse<WorkspaceCompactionResult>.Fail("DELETE_CANCELLED", "操作已取消");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error(
                $"CompactWorkspaceHistory failed for workspace '{workspaceId}'",
                ex
            );
            return BridgeResponse<WorkspaceCompactionResult>.Fail(
                "COMPACTION_FAILED",
                CompactionErrorMessage("COMPACTION_FAILED")
            );
        }
    }

    /// <summary>手动清理工作区历史库备份：只删 .bak 与残留 tmp，主库与 WAL/SHM 永不触碰。</summary>
    public async Task<BridgeResponse<WorkspaceBackupCleanupResult>> DeleteWorkspaceBackup(
        string workspaceId
    )
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return BridgeResponse<WorkspaceBackupCleanupResult>.Fail(
                "INVALID_WORKSPACE_ID",
                "工作区 ID 不能为空"
            );

        try
        {
            var result = await StorageMaintenance
                .DeleteWorkspaceBackupAsync(workspaceId.Trim())
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return BridgeResponse<WorkspaceBackupCleanupResult>.Fail(
                    result.ErrorCode ?? "BACKUP_DELETE_FAILED",
                    BackupCleanupErrorMessage(result.ErrorCode)
                );
            }

            return BridgeResponse<WorkspaceBackupCleanupResult>.Success(
                result,
                result.BackupExisted ? $"历史库备份已清理，已释放 {result.FreedBytes} 字节" : "历史库备份不存在，无需清理"
            );
        }
        catch (OperationCanceledException)
        {
            return BridgeResponse<WorkspaceBackupCleanupResult>.Fail("DELETE_CANCELLED", "操作已取消");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"DeleteWorkspaceBackup failed for workspace '{workspaceId}'", ex);
            return BridgeResponse<WorkspaceBackupCleanupResult>.Fail(
                "BACKUP_DELETE_FAILED",
                BackupCleanupErrorMessage("BACKUP_DELETE_FAILED")
            );
        }
    }

    private static string CompactionErrorMessage(string? code) =>
        code switch
        {
            "INVALID_WORKSPACE_ID" => "工作区 ID 不合法",
            "DATABASE_NOT_FOUND" => "历史库不存在，无需压实",
            "WORKSPACE_BUSY" => "工作区正在执行其他操作，请稍后重试",
            "SESSION_BUSY" => "会话正在生成或执行历史操作，请稍后重试",
            "HISTORY_OPERATION_IN_PROGRESS" => "会话正在执行其他历史操作，请稍后重试",
            "STORAGE_MAINTENANCE_BUSY" => "存储维护正在进行，请稍后重试",
            "CHECKPOINT_BUSY" => "存储正忙，请稍后重试",
            "DATABASE_BUSY" => "数据库正忙，请稍后重试",
            "INSUFFICIENT_DISK_SPACE" => "磁盘空间不足，无法执行压实",
            "DELETE_CANCELLED" => "操作已取消",
            _ => "历史库压实失败，请查看日志后重试",
        };

    private static string BackupCleanupErrorMessage(string? code) =>
        code switch
        {
            "INVALID_WORKSPACE_ID" => "工作区 ID 不合法",
            "WORKSPACE_BUSY" => "工作区正在执行其他操作，请稍后重试",
            "SESSION_BUSY" => "会话正在生成或执行历史操作，请稍后重试",
            "HISTORY_OPERATION_IN_PROGRESS" => "会话正在执行其他历史操作，请稍后重试",
            "STORAGE_MAINTENANCE_BUSY" => "存储维护正在进行，请稍后重试",
            "BACKUP_DELETE_FAILED" => "备份删除失败，文件可能被占用",
            "DELETE_CANCELLED" => "操作已取消",
            _ => "备份清理失败，请查看日志后重试",
        };

    public Task<BridgeResponse<List<StorageEventDto>>> GetRecentHostEvents(int limit = 20)
    {
        try
        {
            var events = StorageEventStore.ReadRecent(_storageManager, Math.Clamp(limit, 1, 200));
            return Task.FromResult(BridgeResponse<List<StorageEventDto>>.Success(events));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetRecentHostEvents failed", ex);
            return Task.FromResult(
                BridgeResponse<List<StorageEventDto>>.Fail("STORAGE_EVENTS_ERROR", ex.Message)
            );
        }
    }

    public async Task<BridgeResponse<StorageValidationResult>> ValidateStoragePath(
        string targetPath
    )
    {
        try
        {
            var result = await _storageManager
                .ValidateTargetRootPathAsync(targetPath)
                .ConfigureAwait(false);
            return BridgeResponse<StorageValidationResult>.Success(result, result.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ValidateStoragePath failed for '{targetPath}'", ex);
            return BridgeResponse<StorageValidationResult>.Fail(
                "STORAGE_VALIDATE_ERROR",
                ex.Message
            );
        }
    }

    public async Task<BridgeResponse<string?>> SelectFolderDialog(string? initialPath = null)
    {
        try
        {
            var selected = await InvokeOnUiThreadAsync<string?>(() =>
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "选择 Halo-Agent 数据与日志根目录",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true,
                        SelectedPath =
                            !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
                                ? initialPath
                                : _storageManager.CurrentDataRoot
                    };

                    var result =
                        _mainForm != null ? dialog.ShowDialog(_mainForm) : dialog.ShowDialog();

                    return
                        result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                        ? dialog.SelectedPath
                        : null;
                })
                .ConfigureAwait(false);

            return BridgeResponse<string?>.Success(selected);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SelectFolderDialog failed", ex);
            return BridgeResponse<string?>.Fail("FOLDER_DIALOG_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<StorageMigrationResult>> ChangeStoragePath(
        string newPath,
        bool migrateData = true
    )
    {
        try
        {
            var result = await _storageManager
                .ChangeDataRootAsync(
                    newPath,
                    migrateData,
                    onProgress: (percent, message) =>
                    {
                        var progressPayload = new
                        {
                            percent,
                            message,
                            targetPath = newPath
                        };
                        JsEventDispatcher.Publish("storage:migration-progress", progressPayload);
                        StorageEventStore.Append(
                            _storageManager,
                            "storage:migration-progress",
                            progressPayload,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        );
                    }
                )
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return BridgeResponse<StorageMigrationResult>.Fail(
                    "STORAGE_CHANGE_FAILED",
                    result.Message
                );
            }

            // 新目录切换成功后，确保新目录下的 network.json 与 providers.json 已初始化并同步至网络工厂
            // need08：新目录切换成功后，确保 agent.json 已初始化
            try
            {
                _agentConfigService.EnsureInitialized();
            }
            catch { }
            try
            {
                _providerConfigService?.EnsureInitialized();
            }
            catch { }
            try
            {
                _promptConfigService.EnsureInitialized();
            }
            catch { }

            var changePayload = new
            {
                oldDataRootPath = result.OldDataRootPath,
                newDataRootPath = result.NewDataRootPath,
                migratedData = result.MigratedData,
                migratedFileCount = result.MigratedFileCount
            };
            JsEventDispatcher.Publish("storage:changed", changePayload);
            StorageEventStore.Append(
                _storageManager,
                "storage:changed",
                changePayload,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );

            return BridgeResponse<StorageMigrationResult>.Success(result, result.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ChangeStoragePath failed for '{newPath}'", ex);
            return BridgeResponse<StorageMigrationResult>.Fail(
                "STORAGE_CHANGE_EXCEPTION",
                ex.Message
            );
        }
    }

    public Task<BridgeResponse<StorageMigrationResult>> ResetStoragePathToDefault(
        bool migrateData = true
    )
    {
        return ChangeStoragePath(_storageManager.DefaultDataRootPath, migrateData);
    }
}
