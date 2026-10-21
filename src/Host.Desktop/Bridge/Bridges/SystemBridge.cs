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
                        JsEventDispatcher.Publish(
                            "storage:migration-progress",
                            new
                            {
                                percent,
                                message,
                                targetPath = newPath
                            }
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

            JsEventDispatcher.Publish(
                "storage:changed",
                new
                {
                    oldDataRootPath = result.OldDataRootPath,
                    newDataRootPath = result.NewDataRootPath,
                    migratedData = result.MigratedData,
                    migratedFileCount = result.MigratedFileCount
                }
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
