using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.Services;
using Host.Desktop.Tools;
using Host.Desktop.Tools.BuiltIn;

namespace Host.Desktop.Startup;

/// <summary>
/// need21-B01: CompositionRoot。Program.Main 只保留 TLS→守卫→Run，其余初始化逐段搬入此处。
/// 单方法 ≤60 行（B01 验收）。
/// </summary>
public static class ServiceRegistration
{
    public static HaloServiceProvider AddHaloAgent(
        StoragePathManager storage,
        Form? mainForm = null,
        bool gpuDisabled = false
    )
    {
        var p = new HaloServiceProvider();
        AddStorage(p, storage);
        AddConfig(p);
        AddGateway(p);
        AddSession(p);
        AddTools(p);
        AddAgent(p);
        AddBridge(p, mainForm, gpuDisabled);
        return p;
    }

    public static void AddStorage(HaloServiceProvider p, StoragePathManager storage)
    {
        p.AddSingleton(storage);
        p.AddSingleton<ISessionRepository>(sp => new SessionRepository(
            sp.GetRequiredService<StoragePathManager>()
        ));
    }

    public static void AddConfig(HaloServiceProvider p)
    {
        p.AddSingleton(sp => new HaloHttpClientFactory());
        p.AddSingleton(sp => new ProviderConfigService(
            sp.GetRequiredService<StoragePathManager>(),
            sp.GetRequiredService<HaloHttpClientFactory>()
        ));
        p.AddSingleton(sp => new PromptConfigService(sp.GetRequiredService<StoragePathManager>()));
        p.AddSingleton(sp => new AgentConfigService(sp.GetRequiredService<StoragePathManager>()));
    }

    public static void AddGateway(HaloServiceProvider p)
    {
        p.AddSingleton<ILlmGatewayClient>(sp => new LlmGatewayClient(
            sp.GetRequiredService<StoragePathManager>(),
            sp.GetRequiredService<HaloHttpClientFactory>()
        ));
    }

    public static void AddSession(HaloServiceProvider p)
    {
        p.AddSingleton<IWorkspaceSessionService>(sp => new WorkspaceSessionService(
            sp.GetRequiredService<ISessionRepository>()
        ));
        p.AddSingleton<ITokenBudgetService>(sp => new TokenBudgetService(
            sp.GetRequiredService<ProviderConfigService>(),
            sp.GetRequiredService<IWorkspaceSessionService>()
        ));
    }

    public static void AddTools(HaloServiceProvider p)
    {
        p.AddSingleton(sp => new ToolRegistry());
    }

    public static void AddAgent(HaloServiceProvider p)
    {
        p.AddSingleton(sp => new UsageMetricsService(sp.GetRequiredService<StoragePathManager>()));
        p.AddSingleton(sp => new SkillMemoryService(sp.GetRequiredService<StoragePathManager>()));
        p.AddSingleton(sp => new SubAgentCoordinator(
            sp.GetRequiredService<IWorkspaceSessionService>(),
            sp.GetRequiredService<ITokenBudgetService>(),
            sp.GetRequiredService<ILlmGatewayClient>(),
            sp.GetRequiredService<StoragePathManager>(),
            sp.GetRequiredService<ProviderConfigService>(),
            sp.GetRequiredService<PromptConfigService>(),
            sp.GetRequiredService<AgentConfigService>(),
            sp.GetRequiredService<UsageMetricsService>()
        ));
        p.AddSingleton<IAgentOrchestrator>(sp => new AgentOrchestrator(
            sp.GetRequiredService<IWorkspaceSessionService>(),
            sp.GetRequiredService<ITokenBudgetService>(),
            sp.GetRequiredService<ILlmGatewayClient>(),
            sp.GetRequiredService<ToolRegistry>(),
            sp.GetRequiredService<StoragePathManager>(),
            sp.GetRequiredService<ProviderConfigService>(),
            sp.GetRequiredService<PromptConfigService>(),
            agentConfigService: sp.GetRequiredService<AgentConfigService>(),
            usageMetrics: sp.GetRequiredService<UsageMetricsService>(),
            coordinator: sp.GetRequiredService<SubAgentCoordinator>(),
            sink: new CefAgentEventSink()
        ));
        p.AddSingleton<ICompactionService>(sp => new CompactionService(
            sp.GetRequiredService<IWorkspaceSessionService>(),
            sp.GetRequiredService<ITokenBudgetService>(),
            sp.GetRequiredService<ILlmGatewayClient>(),
            sp.GetRequiredService<ProviderConfigService>(),
            sp.GetRequiredService<UsageMetricsService>()
        ));
    }

    public static void AddBridge(HaloServiceProvider p, Form? mainForm, bool gpuDisabled)
    {
        SkillWriter.ChangedNotifier = (isSkill, scope, name) =>
        {
            try
            {
                JsEventDispatcher.Publish(
                    "config:skill-memory-changed",
                    new
                    {
                        kind = isSkill ? "skill" : "memory",
                        scope,
                        name
                    }
                );
            }
            catch { }
        };
        p.AddSingleton(mainForm as object ?? new object());
        p.AddSingleton(sp => new HostBridgeFacade(
            mainForm,
            sp.GetRequiredService<StoragePathManager>(),
            gpuDisabled,
            httpClientFactory: sp.GetRequiredService<HaloHttpClientFactory>(),
            providerConfigService: sp.GetRequiredService<ProviderConfigService>(),
            llmGatewayClient: sp.GetRequiredService<ILlmGatewayClient>(),
            sessionRepository: sp.GetRequiredService<ISessionRepository>(),
            workspaceSessionService: sp.GetRequiredService<IWorkspaceSessionService>(),
            tokenBudgetService: sp.GetRequiredService<ITokenBudgetService>(),
            agentOrchestrator: sp.GetRequiredService<IAgentOrchestrator>(),
            compactionService: sp.GetRequiredService<ICompactionService>(),
            promptConfigService: sp.GetRequiredService<PromptConfigService>(),
            agentConfigService: sp.GetRequiredService<AgentConfigService>(),
            skillMemoryService: sp.GetRequiredService<SkillMemoryService>(),
            subCoordinator: sp.GetRequiredService<SubAgentCoordinator>(),
            toolRegistry: sp.GetRequiredService<ToolRegistry>()
        ));
    }
}
