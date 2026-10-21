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

/// <summary>前端 window.hostBridge 门面（need21-B02 薄路由；82 方法已切入 Bridges/*）。</summary>
public sealed partial class HostBridgeFacade : IHostBridgeFacade
{
    private Form? _mainForm;
    private readonly StoragePathManager _storageManager;
    private readonly HaloHttpClientFactory _httpClientFactory;
    private readonly ProviderConfigService _providerConfigService;
    private readonly PromptConfigService _promptConfigService;
    private readonly AgentConfigService _agentConfigService;
    private readonly ILlmGatewayClient _llmGatewayClient;
    private readonly ISessionRepository _sessionRepository;
    private readonly IWorkspaceSessionService _workspaceSessionService;
    private readonly ITokenBudgetService _tokenBudgetService;
    private ToolRegistry? _toolRegistry;
    private IAgentOrchestrator? _agentOrchestrator;
    private ICompactionService? _compactionService;
    private SubAgentCoordinator? _subCoordinator;
    private readonly bool _gpuDisabled;

    internal HaloHttpClientFactory HttpClientFactoryInternal => _httpClientFactory;

    private IAgentOrchestrator AgentOrchestrator =>
        _agentOrchestrator ??= new AgentOrchestrator(
            _workspaceSessionService,
            _tokenBudgetService,
            _llmGatewayClient,
            ToolRegistryInternal,
            _storageManager,
            _providerConfigService,
            _promptConfigService,
            agentConfigService: _agentConfigService,
            usageMetrics: UsageMetricsServiceInternal,
            coordinator: _subCoordinator
        );

    private ICompactionService CompactionService =>
        _compactionService ??= new CompactionService(
            _workspaceSessionService,
            _tokenBudgetService,
            _llmGatewayClient,
            _providerConfigService,
            UsageMetricsServiceInternal
        );

    private ToolRegistry ToolRegistryInternal => _toolRegistry ??= new ToolRegistry();

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
        AgentConfigService? agentConfigService = null,
        SkillMemoryService? skillMemoryService = null,
        SubAgentCoordinator? subCoordinator = null,
        ToolRegistry? toolRegistry = null
    )
    {
        _mainForm = mainForm ?? Application.OpenForms.Cast<Form>().FirstOrDefault();
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _gpuDisabled = gpuDisabled;
        _httpClientFactory = httpClientFactory ?? new HaloHttpClientFactory();
        _providerConfigService = providerConfigService!;
        _promptConfigService = promptConfigService ?? new PromptConfigService(_storageManager);
        _agentConfigService = agentConfigService ?? new AgentConfigService(_storageManager);
        _llmGatewayClient =
            llmGatewayClient ?? new LlmGatewayClient(_storageManager, _httpClientFactory);
        _sessionRepository = sessionRepository ?? new SessionRepository(_storageManager);
        _workspaceSessionService =
            workspaceSessionService ?? new WorkspaceSessionService(_sessionRepository);
        _tokenBudgetService =
            tokenBudgetService
            ?? new TokenBudgetService(_providerConfigService, _workspaceSessionService);
        _agentOrchestrator = agentOrchestrator;
        _compactionService = compactionService;
        _subCoordinator = subCoordinator;
        _toolRegistry = toolRegistry;
        _skillMemoryServiceOpt = skillMemoryService;
    }

    /// <summary>need21-B01: DI 路径回填宿主窗体。</summary>
    public void AttachForm(Form form)
    {
        _mainForm ??= form;
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
            form.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        tcs.SetResult(action());
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                })
            );
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
}
