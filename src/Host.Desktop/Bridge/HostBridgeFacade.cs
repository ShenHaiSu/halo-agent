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

/// <summary>
/// 暴露给前端 window.hostBridge 的异步通信门面。
/// 严格遵守三大通信铁律：
/// 1. 所有耗时/IO 方法返回 Task&lt;BridgeResponse&lt;T&gt;&gt;；
/// 2. 所有涉及 WinForms 控件/对话框操作强制通过 InvokeOnUiThreadAsync 切回主线程；
/// 3. DTO 统一小驼峰序列化。
/// </summary>
  public sealed partial class HostBridgeFacade
  {
    private readonly Form? _mainForm;
    private readonly StoragePathManager _storageManager;
    private readonly HaloHttpClientFactory _httpClientFactory;
     private readonly ProviderConfigService _providerConfigService;
     private readonly PromptConfigService _promptConfigService;
     private readonly AgentConfigService _agentConfigService;
    private readonly ILlmGatewayClient _llmGatewayClient;
    private readonly ISessionRepository _sessionRepository;
    private readonly IWorkspaceSessionService _workspaceSessionService;
    private readonly ITokenBudgetService _tokenBudgetService;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeSessionTokens = new();
    private ToolRegistry? _toolRegistry;
    private IAgentOrchestrator? _agentOrchestrator;
    private ICompactionService? _compactionService;
    private readonly bool _gpuDisabled;

    public HaloHttpClientFactory HttpClientFactory => _httpClientFactory;
     public ProviderConfigService ProviderConfigService => _providerConfigService;
     public PromptConfigService PromptConfigService => _promptConfigService;
     public AgentConfigService AgentConfigService => _agentConfigService;
     public ILlmGatewayClient LlmGatewayClient => _llmGatewayClient;
     public ISessionRepository SessionRepository => _sessionRepository;
     public IWorkspaceSessionService WorkspaceSessionService => _workspaceSessionService;
     public ITokenBudgetService TokenBudgetService => _tokenBudgetService;
     public IAgentOrchestrator AgentOrchestrator => _agentOrchestrator ??= new AgentOrchestrator(_workspaceSessionService, _tokenBudgetService, _llmGatewayClient, ToolRegistry, _storageManager, _providerConfigService, _promptConfigService, agentConfigService: _agentConfigService);
    public ICompactionService CompactionService => _compactionService ??= new CompactionService(_workspaceSessionService, _tokenBudgetService, _llmGatewayClient, _providerConfigService);

    public HostBridgeFacade(
        Form? mainForm,
        StoragePathManager storageManager,
        bool gpuDisabled = false,
        HaloHttpClientFactory? httpClientFactory = null,
        ProviderConfigService? providerConfigService = null,
        ILlmGatewayClient? llmGatewayClient = null,
        ISessionRepository? sessionRepository = null,
        IWorkspaceSessionService? workspaceSessionService = null,
        ITokenBudgetService? tokenBudgetService = null,
         IAgentOrchestrator? agentOrchestrator = null,
         ICompactionService? compactionService = null,
         PromptConfigService? promptConfigService = null,
         AgentConfigService? agentConfigService = null)
    {
        _mainForm = mainForm ?? Application.OpenForms.Cast<Form>().FirstOrDefault();
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _gpuDisabled = gpuDisabled;
        _httpClientFactory = httpClientFactory ?? new HaloHttpClientFactory();
         _providerConfigService = providerConfigService ?? new ProviderConfigService(_storageManager, _httpClientFactory);
         _promptConfigService = promptConfigService ?? new PromptConfigService(_storageManager);
         _agentConfigService = agentConfigService ?? new AgentConfigService(_storageManager);
         _llmGatewayClient = llmGatewayClient ?? new LlmGatewayClient(_storageManager, _httpClientFactory);
         _sessionRepository = sessionRepository ?? new SessionRepository(_storageManager);
         _workspaceSessionService = workspaceSessionService ?? new WorkspaceSessionService(_sessionRepository);
         _tokenBudgetService = tokenBudgetService ?? new TokenBudgetService(_providerConfigService, _workspaceSessionService);
         _agentOrchestrator = agentOrchestrator;
         _compactionService = compactionService;
    }

    /// <summary>
    /// 跨线程 UI 调度辅助方法：确保任何 WinForms 主窗体或原生对话框操作均在 STAThread 主线程执行。
    /// </summary>
    public Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var form = _mainForm;

        if (form == null || form.IsDisposed)
        {
            tcs.SetCanceled();
            return tcs.Task;
        }

        if (form.InvokeRequired)
        {
            form.BeginInvoke(new Action(() =>
            {
                try
                {
                    tcs.SetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }));
        }
        else
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }

        return tcs.Task;
    }

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
            return Task.FromResult(BridgeResponse<SystemRuntimeInfo>.Fail("SYS_INFO_ERROR", ex.Message));
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
            return Task.FromResult(BridgeResponse<StorageConfigInfo>.Fail("STORAGE_CONFIG_ERROR", ex.Message));
        }
    }

    public async Task<BridgeResponse<StorageValidationResult>> ValidateStoragePath(string targetPath)
    {
        try
        {
            var result = await _storageManager.ValidateTargetRootPathAsync(targetPath).ConfigureAwait(false);
            return BridgeResponse<StorageValidationResult>.Success(result, result.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ValidateStoragePath failed for '{targetPath}'", ex);
            return BridgeResponse<StorageValidationResult>.Fail("STORAGE_VALIDATE_ERROR", ex.Message);
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
                    SelectedPath = !string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)
                        ? initialPath
                        : _storageManager.CurrentDataRoot
                };

                var result = _mainForm != null
                    ? dialog.ShowDialog(_mainForm)
                    : dialog.ShowDialog();

                return result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                    ? dialog.SelectedPath
                    : null;
            }).ConfigureAwait(false);

            return BridgeResponse<string?>.Success(selected);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SelectFolderDialog failed", ex);
            return BridgeResponse<string?>.Fail("FOLDER_DIALOG_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<StorageMigrationResult>> ChangeStoragePath(string newPath, bool migrateData = true)
    {
        try
        {
            var result = await _storageManager.ChangeDataRootAsync(
                newPath,
                migrateData,
                onProgress: (percent, message) =>
                {
                    JsEventDispatcher.Publish("storage:migration-progress", new
                    {
                        percent,
                        message,
                        targetPath = newPath
                    });
                }).ConfigureAwait(false);

            if (!result.Success)
            {
                return BridgeResponse<StorageMigrationResult>.Fail("STORAGE_CHANGE_FAILED", result.Message);
            }

            // 新目录切换成功后，确保新目录下的 network.json 与 providers.json 已初始化并同步至网络工厂
            // need08：新目录切换成功后，确保 agent.json 已初始化
            try { _agentConfigService.EnsureInitialized(); } catch { }
             _providerConfigService.EnsureInitialized();
             try { _promptConfigService.EnsureInitialized(); } catch { }

            JsEventDispatcher.Publish("storage:changed", new
            {
                oldDataRootPath = result.OldDataRootPath,
                newDataRootPath = result.NewDataRootPath,
                migratedData = result.MigratedData,
                migratedFileCount = result.MigratedFileCount
            });

            return BridgeResponse<StorageMigrationResult>.Success(result, result.Message);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ChangeStoragePath failed for '{newPath}'", ex);
            return BridgeResponse<StorageMigrationResult>.Fail("STORAGE_CHANGE_EXCEPTION", ex.Message);
        }
    }

    public Task<BridgeResponse<StorageMigrationResult>> ResetStoragePathToDefault(bool migrateData = true)
    {
        return ChangeStoragePath(_storageManager.DefaultDataRootPath, migrateData);
    }

    public Task<BridgeResponse<bool>> OpenPathInExplorer(string targetPath)
    {
        try
        {
            var pathToOpen = string.IsNullOrWhiteSpace(targetPath)
                ? _storageManager.CurrentDataRoot
                : Path.GetFullPath(targetPath);

            if (File.Exists(pathToOpen))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{pathToOpen}\"",
                    UseShellExecute = true
                });
                return Task.FromResult(BridgeResponse<bool>.Success(true));
            }

            if (!Directory.Exists(pathToOpen))
            {
                Directory.CreateDirectory(pathToOpen);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{pathToOpen}\"",
                UseShellExecute = true
            });

            return Task.FromResult(BridgeResponse<bool>.Success(true));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"OpenPathInExplorer failed for '{targetPath}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("OPEN_EXPLORER_ERROR", ex.Message));
        }
    }

    public async Task<BridgeResponse<int>> CleanupExpiredLogs(int retentionDays = 14)
    {
        try
        {
            var effectiveDays = Math.Clamp(retentionDays, 1, 365);
            var settings = _storageManager.LoadAppSettings();
            settings.LogRetentionDays = effectiveDays;
            await _storageManager.SaveAppSettingsAsync(settings).ConfigureAwait(false);

            var deleted = HaloLogger.CleanupExpiredLogs(effectiveDays);
            HaloLogger.App.Info($"Manual log cleanup executed (retentionDays={effectiveDays}, deleted={deleted}).");
            await HaloLogger.FlushAsync().ConfigureAwait(false);
            return BridgeResponse<int>.Success(deleted, $"清理完成，共移除 {deleted} 个过期日志文件。");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("CleanupExpiredLogs failed", ex);
            return BridgeResponse<int>.Fail("LOG_CLEANUP_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<bool>> WriteDiagnosticLog(string channel, string message)
    {
        try
        {
            var normalized = (channel ?? "app").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "llm":
                    HaloLogger.Llm.Info(message);
                    break;
                case "tool":
                    HaloLogger.Tool.Info(message);
                    break;
                default:
                    HaloLogger.App.Info(message);
                    break;
            }

            await HaloLogger.FlushAsync().ConfigureAwait(false);
            return BridgeResponse<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return BridgeResponse<bool>.Fail("LOG_WRITE_ERROR", ex.Message);
        }
    }

    public async Task<BridgeResponse<bool>> SetWindowTitle(string title)
    {
        try
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (_mainForm != null && !_mainForm.IsDisposed)
                {
                    _mainForm.Text = string.IsNullOrWhiteSpace(title) ? "Halo-Agent Desktop" : title;
                }
                return true;
            }).ConfigureAwait(false);

            return BridgeResponse<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return BridgeResponse<bool>.Fail("WINDOW_TITLE_ERROR", ex.Message);
        }
    }

    // =========================================================================
    // Stage 02: 全局网络代理中枢 (ProxyHub) 与多服务商配置引擎 IPC 接口
    // =========================================================================

    public Task<BridgeResponse<GlobalNetworkSettings>> GetNetworkSettings()
    {
        try
        {
            var settings = _providerConfigService.GetNetworkSettingsForClient();
            return Task.FromResult(BridgeResponse<GlobalNetworkSettings>.Success(settings));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetNetworkSettings failed", ex);
            return Task.FromResult(BridgeResponse<GlobalNetworkSettings>.Fail("NETWORK_GET_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<GlobalNetworkSettings>> SaveNetworkSettings(string settingsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                return Task.FromResult(BridgeResponse<GlobalNetworkSettings>.Fail("NETWORK_SAVE_INVALID", "网络配置参数不能为空"));
            }

            var incoming = JsonSerializer.Deserialize<GlobalNetworkSettings>(settingsJson, HaloJsonOptions.IndentedCamelCase);
            if (incoming == null)
            {
                return Task.FromResult(BridgeResponse<GlobalNetworkSettings>.Fail("NETWORK_SAVE_INVALID", "网络配置 JSON 解析失败"));
            }

            var saved = _providerConfigService.SaveNetworkSettings(incoming);
            JsEventDispatcher.Publish("config:network-changed", saved);
            return Task.FromResult(BridgeResponse<GlobalNetworkSettings>.Success(saved, "网络与代理设置已保存并立即生效"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SaveNetworkSettings failed", ex);
            return Task.FromResult(BridgeResponse<GlobalNetworkSettings>.Fail("NETWORK_SAVE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<SystemProxySnapshot>> InspectSystemProxy(string? probeTargetUrl = null)
    {
        try
        {
            string target = string.IsNullOrWhiteSpace(probeTargetUrl) ? "https://api.openai.com" : probeTargetUrl.Trim();
            var snapshot = SystemProxyInspector.InspectCurrentSystemProxy(target);
            return Task.FromResult(BridgeResponse<SystemProxySnapshot>.Success(snapshot));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("InspectSystemProxy failed", ex);
            return Task.FromResult(BridgeResponse<SystemProxySnapshot>.Fail("PROXY_INSPECT_ERROR", ex.Message));
        }
    }

    public async Task<BridgeResponse<ProxyConnectivityTestResult>> TestProxyConnectivity(
        string? targetUrl = null,
        string? overrideSettingsJson = null,
        string? providerJson = null)
    {
        try
        {
            GlobalNetworkSettings? overrideSettings = null;
            if (!string.IsNullOrWhiteSpace(overrideSettingsJson))
            {
                overrideSettings = JsonSerializer.Deserialize<GlobalNetworkSettings>(overrideSettingsJson, HaloJsonOptions.IndentedCamelCase);
            }

            ProviderConfig? provider = null;
            if (!string.IsNullOrWhiteSpace(providerJson))
            {
                provider = JsonSerializer.Deserialize<ProviderConfig>(providerJson, HaloJsonOptions.IndentedCamelCase);
                if (provider != null && string.IsNullOrWhiteSpace(provider.ApiKeyEncrypted) && !string.IsNullOrWhiteSpace(provider.Id))
                {
                    var persisted = _providerConfigService.GetProviderById(provider.Id);
                    if (persisted != null)
                    {
                        provider.ApiKeyEncrypted = persisted.ApiKeyEncrypted;
                    }
                }
            }

            var result = await _httpClientFactory.TestProxyConnectivityAsync(
                targetUrl: targetUrl,
                overrideGlobalSettings: overrideSettings,
                provider: provider).ConfigureAwait(false);

            return BridgeResponse<ProxyConnectivityTestResult>.Success(
                result,
                result.Success
                    ? $"连通测试成功 ({result.ElapsedMs} ms · {result.EffectiveProxyUsed})"
                    : result.ErrorMessage ?? "连通测试失败");
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("TestProxyConnectivity failed", ex);
            return BridgeResponse<ProxyConnectivityTestResult>.Fail("PROXY_TEST_ERROR", ex.Message);
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> GetProvidersConfig()
    {
        try
        {
            var doc = _providerConfigService.GetProvidersConfigForClient();
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Success(doc));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetProvidersConfig failed", ex);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDERS_GET_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> SaveProvidersConfig(string configJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configJson))
            {
                return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDERS_SAVE_INVALID", "Provider 配置参数不能为空"));
            }

            var incoming = JsonSerializer.Deserialize<ProvidersConfigDocument>(configJson, HaloJsonOptions.IndentedCamelCase);
            if (incoming == null)
            {
                return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDERS_SAVE_INVALID", "Provider 配置 JSON 解析失败"));
            }

            var saved = _providerConfigService.SaveProvidersConfig(incoming);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Success(saved, "大模型服务商与模型配置已安全加密落盘"));
        }
        catch (ProviderConfigValidationException vex)
        {
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_VALIDATION", $"字段 [{vex.Field}]：{vex.Message}"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SaveProvidersConfig failed", ex);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDERS_SAVE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> UpsertProvider(string providerJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(providerJson))
            {
                return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_UPSERT_INVALID", "Provider 参数不能为空"));
            }

            var provider = JsonSerializer.Deserialize<ProviderConfig>(providerJson, HaloJsonOptions.IndentedCamelCase);
            if (provider == null)
            {
                return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_UPSERT_INVALID", "Provider JSON 解析失败"));
            }

            var saved = _providerConfigService.UpsertProvider(provider);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Success(saved, $"服务商 [{provider.Name}] 已保存"));
        }
        catch (ProviderConfigValidationException vex)
        {
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_VALIDATION", $"字段 [{vex.Field}]：{vex.Message}"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("UpsertProvider failed", ex);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_UPSERT_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> DeleteProvider(string providerId)
    {
        try
        {
            var saved = _providerConfigService.DeleteProvider(providerId);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Success(saved, "服务商已删除"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"DeleteProvider failed for '{providerId}'", ex);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("PROVIDER_DELETE_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<ProvidersConfigDocument>> SetActiveModel(string providerId, string modelId)
    {
        try
        {
            var saved = _providerConfigService.SetActiveModel(providerId, modelId);
            JsEventDispatcher.Publish("config:providers-changed", saved);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Success(saved, $"已切换至模型 {modelId}"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("SetActiveModel failed", ex);
            return Task.FromResult(BridgeResponse<ProvidersConfigDocument>.Fail("ACTIVE_MODEL_ERROR", ex.Message));
        }
    }

    public Task<BridgeResponse<ProviderEndpointPreviewDto>> PreviewProviderResolution(
        string providerJson,
        string? modelId = null,
        string? sessionId = null,
        string? workspacePath = null)
    {
        try
        {
            var provider = JsonSerializer.Deserialize<ProviderConfig>(providerJson, HaloJsonOptions.IndentedCamelCase);
            if (provider == null)
            {
                return Task.FromResult(BridgeResponse<ProviderEndpointPreviewDto>.Fail("PREVIEW_INVALID", "Provider JSON 解析失败"));
            }

            var preview = _providerConfigService.PreviewProviderEndpointsAndHeaders(provider, modelId, sessionId, workspacePath);
            return Task.FromResult(BridgeResponse<ProviderEndpointPreviewDto>.Success(preview));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("PreviewProviderResolution failed", ex);
            return Task.FromResult(BridgeResponse<ProviderEndpointPreviewDto>.Fail("PREVIEW_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// 前端发起会话流式请求：
    /// 后台启动 IAsyncEnumerable 管道消费并将事件转换为 SessionStreamEventDto 通过 JsEventDispatcher 推送至前端。
    /// </summary>
    public Task<BridgeResponse<string>> StartSessionRun(string requestJson)
    {
        try
        {
            var req = JsonSerializer.Deserialize<UnifiedLlmRequest>(requestJson, HaloJsonOptions.IndentedCamelCase);
            if (req == null)
            {
                return Task.FromResult(BridgeResponse<string>.Fail("INVALID_REQUEST", "请求 JSON 无法反序列化"));
            }

            string sessionId = !string.IsNullOrWhiteSpace(req.HeaderContext?.SessionId)
                ? req.HeaderContext.SessionId
                : req.RequestId;

            // 取消可能已经存在的旧会话生成任务
            if (_activeSessionTokens.TryRemove(sessionId, out var oldCts))
            {
                try
                {
                    oldCts.Cancel();
                    oldCts.Dispose();
                }
                catch
                {
                    // 忽略旧任务清理异常
                }
            }

            var cts = new CancellationTokenSource();
            _activeSessionTokens[sessionId] = cts;

            // 在独立后台线程运行流式消费，不阻塞 Bridge 响应
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var evt in _llmGatewayClient.StreamAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        var dto = SessionStreamEventDto.FromUnifiedEvent(sessionId, evt);
                        JsEventDispatcher.Publish("llm:stream-event", dto);

                        if (evt.Kind == UnifiedStreamEventKind.StreamCompleted ||
                            evt.Kind == UnifiedStreamEventKind.StreamCancelled ||
                            evt.Kind == UnifiedStreamEventKind.StreamError)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    var cancelDto = new SessionStreamEventDto
                    {
                        SessionId = sessionId,
                        Kind = nameof(UnifiedStreamEventKind.StreamCancelled)
                    };
                    JsEventDispatcher.Publish("llm:stream-event", cancelDto);
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Error($"[HostBridge] Session {sessionId} 流式推送异常", ex);
                    var errDto = new SessionStreamEventDto
                    {
                        SessionId = sessionId,
                        Kind = nameof(UnifiedStreamEventKind.StreamError),
                        ErrorCode = "STREAM_EXECUTION_EXCEPTION",
                        ErrorMessage = ex.Message
                    };
                    JsEventDispatcher.Publish("llm:stream-event", errDto);
                }
                finally
                {
                    _activeSessionTokens.TryRemove(sessionId, out _);
                    cts.Dispose();
                }
            }, CancellationToken.None);

            return Task.FromResult(BridgeResponse<string>.Success(sessionId, "流式请求已启动"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("StartSessionRun failed", ex);
            return Task.FromResult(BridgeResponse<string>.Fail("SESSION_START_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// 取消正在运行的会话流式请求：立即触发关联的 CancellationTokenSource.Cancel()，并在 100ms 内切断网络连接。
    /// </summary>
    public Task<BridgeResponse<bool>> CancelSessionRun(string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(BridgeResponse<bool>.Fail("INVALID_SESSION_ID", "会话 ID 不能为空"));
            }

            if (_activeSessionTokens.TryRemove(sessionId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                HaloLogger.App.Info($"[HostBridge] 已成功请求取消 Session {sessionId} 的生成");
                return Task.FromResult(BridgeResponse<bool>.Success(true, "已发送取消指令"));
            }

            return Task.FromResult(BridgeResponse<bool>.Success(false, "没有正在运行的会话任务"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"CancelSessionRun failed for '{sessionId}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("CANCEL_ERROR", ex.Message));
        }
    }
    /// <summary>
    /// Stage 06 Agent ReAct 编排入口：后台 Task 运行 AgentOrchestrator，不阻塞 CEF 线程；
    /// 每 session 一个 CancellationTokenSource（复用 _activeSessionTokens，key 为 sessionId）。
    /// </summary>
    public Task<BridgeResponse<string>> StartAgentTurn(
        string workspaceId,
        string sessionId,
        string userText,
        string? providerId = null,
        string? modelId = null,
        string? attachmentsJson = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(BridgeResponse<string>.Fail("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空"));
            }
            if (string.IsNullOrWhiteSpace(userText))
            {
                return Task.FromResult(BridgeResponse<string>.Fail("EMPTY_PROMPT", "用户输入不能为空"));
            }
            string turnKey = "agent:" + sessionId.Trim();
            if (_activeSessionTokens.TryRemove(turnKey, out var oldCts))
            {
                try { oldCts.Cancel(); oldCts.Dispose(); } catch { /* ignore */ }
            }
            var cts = new CancellationTokenSource();
            _activeSessionTokens[turnKey] = cts;
            string wsId = workspaceId.Trim();
            string sessId = sessionId.Trim();
            string prompt = userText;
            string? provId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
            string? modId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
            try
            {
                var imgRefs = SaveTurnAttachments(wsId, sessId, attachmentsJson, ref prompt);
                if (imgRefs.Count > 0) PendingSessionImages.Put(sessId, imgRefs);
            }
            catch (Exception ex)
            {
                HaloLogger.App.Warn("[HostBridge] 附件落盘失败（文字仍发送）：" + ex.Message);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await AgentOrchestrator.RunAgentTurnAsync(wsId, sessId, prompt, provId, modId, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    JsEventDispatcher.Publish("agent:turn-event", new { sessionId = sessId, kind = "TurnCancelled" });
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Error($"[HostBridge] Agent turn failed for session '{sessId}'", ex);
                    JsEventDispatcher.Publish("agent:turn-event", new { sessionId = sessId, kind = "TurnError", payload = new { errorMessage = ex.Message } });
                }
                finally
                {
                    _activeSessionTokens.TryRemove(turnKey, out _);
                    cts.Dispose();
                }
            }, CancellationToken.None);

            return Task.FromResult(BridgeResponse<string>.Success(sessId, "Agent 推理已启动"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("StartAgentTurn failed", ex);
            return Task.FromResult(BridgeResponse<string>.Fail("AGENT_START_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// 取消正在运行的 Agent ReAct 循环：触发 CancellationTokenSource.Cancel() 并杀子进程（经 ToolContext 传递）。
    /// </summary>
    public Task<BridgeResponse<bool>> CancelAgentTurn(string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(BridgeResponse<bool>.Fail("INVALID_SESSION_ID", "会话 ID 不能为空"));
            }

            string turnKey = "agent:" + sessionId.Trim();
            if (_activeSessionTokens.TryRemove(turnKey, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                HaloLogger.App.Info($"[HostBridge] 已请求取消 Session {sessionId} 的 Agent 推理");
                return Task.FromResult(BridgeResponse<bool>.Success(true, "已发送取消指令"));
            }

            return Task.FromResult(BridgeResponse<bool>.Success(false, "没有正在运行的 Agent 任务"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"CancelAgentTurn failed for '{sessionId}'", ex);
            return Task.FromResult(BridgeResponse<bool>.Fail("CANCEL_ERROR", ex.Message));
        }
    }


    #region Stage 04: Workspace, Session & Token Budget Methods

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

    public async Task<BridgeResponse<WorkspaceInfo>> AddWorkspace(string rootPath, string? name = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return BridgeResponse<WorkspaceInfo>.Fail("工作路径不能为空", "INVALID_PATH");
            }
            var ws = await _workspaceSessionService.AddOrUpdateWorkspaceAsync(rootPath, name).ConfigureAwait(false);
            return BridgeResponse<WorkspaceInfo>.Success(ws);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"AddWorkspace failed for path '{rootPath}'", ex);
            return BridgeResponse<WorkspaceInfo>.Fail(ex.Message, "WORKSPACE_ERROR");
        }
    }

    public async Task<BridgeResponse<bool>> RemoveWorkspace(string workspaceId)
    {
        try
        {
            bool removed = await _workspaceSessionService.RemoveWorkspaceAsync(workspaceId).ConfigureAwait(false);
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
            var list = await _workspaceSessionService.GetSessionsByWorkspaceAsync(workspaceId).ConfigureAwait(false);
            return BridgeResponse<List<SessionMetadata>>.Success(list);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GetSessions failed for workspace '{workspaceId}'", ex);
            return BridgeResponse<List<SessionMetadata>>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<SessionMetadata>> CreateSession(string workspaceId, string? title = null, string? providerId = null, string? modelId = null)
    {
        try
        {
            var meta = await _workspaceSessionService.CreateSessionAsync(workspaceId, title, providerId, modelId).ConfigureAwait(false);
            return BridgeResponse<SessionMetadata>.Success(meta);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"CreateSession failed in workspace '{workspaceId}'", ex);
            return BridgeResponse<SessionMetadata>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<List<UnifiedChatMessage>>> GetSessionMessages(string workspaceId, string sessionId)
    {
        try
        {
            var messages = await _workspaceSessionService.GetSessionMessagesAsync(workspaceId, sessionId).ConfigureAwait(false);
            return BridgeResponse<List<UnifiedChatMessage>>.Success(messages);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GetSessionMessages failed for session '{sessionId}'", ex);
            return BridgeResponse<List<UnifiedChatMessage>>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<bool>> AppendSessionMessage(string workspaceId, string sessionId, string messageJson)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<UnifiedChatMessage>(messageJson, HaloJsonOptions.IndentedCamelCase);
            if (msg == null)
            {
                return BridgeResponse<bool>.Fail("消息 JSON 无法反序列化", "INVALID_MESSAGE");
            }
            bool ok = await _workspaceSessionService.AppendSessionMessageAsync(workspaceId, sessionId, msg).ConfigureAwait(false);
            return BridgeResponse<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"AppendSessionMessage failed for session '{sessionId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<bool>> UpdateSessionTitle(string workspaceId, string sessionId, string title)
    {
        try
        {
            bool ok = await _workspaceSessionService.UpdateSessionTitleAsync(workspaceId, sessionId, title).ConfigureAwait(false);
            return BridgeResponse<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"UpdateSessionTitle failed for session '{sessionId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<bool>> SetSessionPinned(string workspaceId, string sessionId, bool isPinned)
    {
        try
        {
            bool ok = await _workspaceSessionService.SetSessionPinnedAsync(workspaceId, sessionId, isPinned).ConfigureAwait(false);
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
            bool ok = await _workspaceSessionService.DeleteSessionAsync(workspaceId, sessionId).ConfigureAwait(false);
            return BridgeResponse<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"DeleteSession failed for session '{sessionId}'", ex);
            return BridgeResponse<bool>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<string>> ExportSessionMarkdown(string workspaceId, string sessionId)
    {
        try
        {
            string md = await _workspaceSessionService.ExportSessionMarkdownAsync(workspaceId, sessionId).ConfigureAwait(false);
            return BridgeResponse<string>.Success(md);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ExportSessionMarkdown failed for session '{sessionId}'", ex);
            return BridgeResponse<string>.Fail(ex.Message, "SESSION_ERROR");
        }
    }

    public async Task<BridgeResponse<ContextBudgetStatusDto>> GetContextBudget(string providerId, string modelId, string workspaceId, string sessionId)
    {
        try
        {
            var status = await _tokenBudgetService.GetBudgetStatusAsync(providerId, modelId, workspaceId, sessionId).ConfigureAwait(false);
            return BridgeResponse<ContextBudgetStatusDto>.Success(status);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("GetContextBudget failed", ex);
            return BridgeResponse<ContextBudgetStatusDto>.Fail(ex.Message, "BUDGET_ERROR");
        }
    }
    #region Stage 05: Standard Toolchain Debug Methods

    public ToolRegistry ToolRegistry => _toolRegistry ??= new ToolRegistry();

    /// <summary>
    /// Stage 05 前端调试：导出工具定义 Schema（protocol: chat | resp）。
    /// </summary>
    public Task<BridgeResponse<string>> GetToolDefinitions(string protocol = "chat")
    {
        try
        {
            string normalized = (protocol ?? "chat").Trim().ToLowerInvariant();
            string json = normalized == "resp"
                ? ToolRegistry.GetResponsesToolsJson()
                : ToolRegistry.GetChatToolsJson();
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
        string? toolCallId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return BridgeResponse<ToolResult>.Fail("INVALID_TOOL", "工具名不能为空");
            }

            var ws = await _workspaceSessionService.GetWorkspaceAsync(workspaceId).ConfigureAwait(false);
            if (ws == null)
            {
                return BridgeResponse<ToolResult>.Fail("WORKSPACE_NOT_FOUND", $"工作区不存在：'{workspaceId}'");
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
            }
            catch { /* 调试单跑快照失败则回退常量，不阻断 */ }
            var call = new ToolCallDto(
                string.IsNullOrWhiteSpace(toolCallId) ? $"call_{Guid.NewGuid():N}"[..12] : toolCallId.Trim(),
                toolName.Trim(),
                argumentsJson);
            ToolResult result = await ToolRegistry.DispatchAsync(call, context).ConfigureAwait(false);
            return BridgeResponse<ToolResult>.Success(result);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"ExecuteTool failed for '{toolName}'", ex);
            return BridgeResponse<ToolResult>.Fail("TOOL_EXEC_ERROR", ex.Message);
        }
    }

    #endregion

    #region need05 Compact: 上下文压缩编排（后台 Task + CompactStarted/Completed/Failed 事件）

    /// <summary>
    /// need05-03：/compact 入口。后台 Task 运行压缩，不阻塞 CEF 线程；
    /// 前后发布 agent:turn-event（CompactStarted → CompactCompleted/CompactFailed）。
    /// 不复用 _activeSessionTokens 的 agent: 键（避免与推理互相取消），另起 compact: 键且不提供 Cancel 入口。
    /// </summary>
    public Task<BridgeResponse<CompactionResult>> CompactSession(
        string workspaceId,
        string sessionId,
        string? providerId = null,
        string? modelId = null,
        int keepLastGroups = 6)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return Task.FromResult(BridgeResponse<CompactionResult>.Fail("INVALID_SESSION", "工作区 ID 与会话 ID 不能为空"));
            }

            string wsId = workspaceId.Trim();
            string sessId = sessionId.Trim();
            string? provId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
            string? modId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
            int keep = keepLastGroups < 1 ? 6 : keepLastGroups;
            string compactKey = "compact:" + sessId;

            if (_activeSessionTokens.ContainsKey(compactKey))
            {
                return Task.FromResult(BridgeResponse<CompactionResult>.Fail("COMPACT_BUSY", "压缩进行中，请稍候"));
            }

            JsEventDispatcher.Publish("agent:turn-event", new
            {
                sessionId = sessId,
                kind = "CompactStarted",
                payload = new { sessionId = sessId },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            _ = Task.Run(async () =>
            {
                var cts = new CancellationTokenSource();
                _activeSessionTokens[compactKey] = cts;
                try
                {
                    var result = await CompactionService.CompactAsync(wsId, sessId, provId, modId, keep, cts.Token).ConfigureAwait(false);
                    if (result.Success)
                    {
                        JsEventDispatcher.Publish("agent:turn-event", new
                        {
                            sessionId = sessId,
                            kind = "CompactCompleted",
                            payload = new
                            {
                                removedMessages = result.RemovedMessages,
                                keptMessages = result.KeptMessages,
                                summaryPreview = result.SummaryPreview,
                                backupFileName = result.BackupFileName,
                                updatedAtUtc = DateTime.UtcNow.ToString("o")
                            },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        });
                    }
                    else
                    {
                        JsEventDispatcher.Publish("agent:turn-event", new
                        {
                            sessionId = sessId,
                            kind = "CompactFailed",
                            payload = new { errorCode = result.ErrorCode, errorMessage = result.ErrorMessage },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        });
                    }
                }
                catch (Exception ex)
                {
                    HaloLogger.App.Error($"[HostBridge] Compact failed for session '{sessId}'", ex);
                    JsEventDispatcher.Publish("agent:turn-event", new
                    {
                        sessionId = sessId,
                        kind = "CompactFailed",
                        payload = new { errorCode = "COMPACT_EXCEPTION", errorMessage = ex.Message },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }
                finally
                {
                    _activeSessionTokens.TryRemove(compactKey, out _);
                    cts.Dispose();
                }
            }, CancellationToken.None);

            return Task.FromResult(BridgeResponse<CompactionResult>.Success(default!, "压缩已启动"));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("CompactSession failed", ex);
            return Task.FromResult(BridgeResponse<CompactionResult>.Fail("COMPACT_START_ERROR", ex.Message));
        }
    }

    /// <summary>need05-03：列出压缩备份文件名（审计用），返回 JSON 数组字符串。</summary>
    public Task<BridgeResponse<string>> GetCompactBackupList(string workspaceId, string sessionId)
    {
        try
        {
            string sessionDir = _sessionRepository.GetSessionDirectory(workspaceId, sessionId);
            var names = new List<string>();
            if (Directory.Exists(sessionDir))
            {
                names = Directory.GetFiles(sessionDir, "messages.compact-bak-*.jsonl")
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderByDescending(n => n, StringComparer.Ordinal)
                    .ToList();
            }
            string json = JsonSerializer.Serialize(names, HaloJsonOptions.IndentedCamelCase);
            return Task.FromResult(BridgeResponse<string>.Success(json));
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error($"GetCompactBackupList failed for session '{sessionId}'", ex);
            return Task.FromResult(BridgeResponse<string>.Fail("COMPACT_BACKUP_LIST_ERROR", ex.Message));
        }
    }

    #endregion

    #endregion
}
