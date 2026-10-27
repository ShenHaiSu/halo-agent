using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Types;
using HaloAgent.Engine.Core.Agent;
using HaloAgent.Engine.Core.Events;
using HaloAgent.Engine.Core.Llm;
using HaloAgent.Engine.Core.Tools;
using HaloAgent.Engine.Storage;
using HaloAgent.Engine.Storage.Config;
using HaloAgent.Engine.Storage.Sessions;
using Microsoft.Extensions.Logging;

namespace HaloAgent.Engine.Server.Routing;

/// <summary>
/// P2 装配产物：一组互相连好的对象（路由 / 状态 / 总线 / 会话 / 存储 / 工具）。
/// 由 FrameServer 驱动；释放时关会话与存储（总线由 FrameServer 收尾）。
/// </summary>
public sealed class AssembledEngine : IAsyncDisposable
{
    public AssembledEngine(
        MethodRouter router,
        EngineState state,
        EventBus bus,
        AgentSession session,
        SessionStore store,
        ToolRegistry tools)
    {
        Router = router;
        State = state;
        Bus = bus;
        Session = session;
        Store = store;
        Tools = tools;
    }

    public MethodRouter Router { get; }
    public EngineState State { get; }
    public EventBus Bus { get; }
    public AgentSession Session { get; }
    public SessionStore Store { get; }
    public ToolRegistry Tools { get; }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync().ConfigureAwait(false);
        Store.Dispose();
    }
}

/// <summary>
/// P2 装配：把 LLM / 工具 / 存储 / 会话 / 路由连起来（见 05 §7.3 装配清单）。
/// 未配置模型等失败时抛异常，由调用方决定回落到 P1 stub。
/// </summary>
public static class EngineAssembly
{
    public static AssembledEngine BuildRouter(
        IFrameChannel channel,
        string dataDir,
        ILogger busLogger,
        ILogger methodsLogger,
        CancellationToken shutdownCt)
    {
        // 数据目录对齐：--data-dir 进来时让 Paths 跟上（SessionStore/Config/Undo 都从它取值）
        if (!string.IsNullOrWhiteSpace(dataDir))
            Environment.SetEnvironmentVariable("HALO_DATA_DIR", dataDir.Trim());

        Paths.EnsureDataDirs();

        // 1) 建 llm
        var cfg = ConfigStore.Load();
        var resolved = ConfigStore.Resolve(cfg);
        var llm = new OpenAiCompatibleClient(new LlmConfig
        {
            BaseUrl = resolved.BaseUrl,
            Model = resolved.Model,
            ApiKey = resolved.ApiKey,
            ProxyUrl = resolved.ProxyUrl,
            Headers = resolved.Headers,
        });

        // 2) 建工具
        var tools = ToolRegistry.CreateBuiltin();

        // 3) 建存储
        var store = new SessionStore();
        store.StartNew();

        // 4) 建通道总线 + 会话（有管道 = 有 UI，可交互确认）
        var bus = new EventBus(channel, busLogger);
        var session = new AgentSession(
            llm, tools, store, bus,
            Paths.DataDir, Environment.CurrentDirectory, PermissionMode.Ask, resolved.Persona,
            resolved.MaxTurns, resolved.Retry,
            askInteractive: true);

        // 5) 状态（给 stub 的 system.* 用）+ 同步已解析的配置
        var router = new MethodRouter();
        var state = new EngineState(Paths.DataDir, router)
        {
            Model = resolved.Model,
            Provider = resolved.ProviderName,
            BaseUrl = resolved.BaseUrl,
            ApiKeySet = !string.IsNullOrEmpty(resolved.ApiKey),
            ContextWindow = resolved.ContextWindow,
            MaxTurns = resolved.MaxTurns,
            Retry = resolved.Retry,
            Persona = resolved.Persona,
        };

        // 6) 注册路由（P1 stub → P2 真实，见 §7.3 清单）
        // 6) 注册路由（P1 stub → P2 真实，见 §7.3 清单；P07 已全部 real 化，零 stub）
        var real = new RealMethods(session, store, tools, Paths.DataDir, methodsLogger, shutdownCt);
        var stub = new StubMethods(state);
        router.RegisterAll(
            ("system.ping", stub.SystemPing),
            ("system.info", stub.SystemInfo),
            ("system.protocol", stub.SystemProtocol),
            ("system.shutdown", stub.SystemShutdown),
            ("system.diagnostics", real.SystemDiagnosticsAsync),

            ("agent.prompt", real.AgentPromptAsync),
            ("agent.queue", real.AgentQueueAsync),
            ("agent.unqueue", real.AgentUnqueueAsync),
            ("agent.cancel", real.AgentCancelAsync),
            ("agent.answer", real.AgentAnswerAsync),
            ("agent.drain", real.AgentDrainAsync),
            ("agent.resync", real.StateSnapshotAsync),

            ("session.new", real.SessionNewAsync),
            ("session.resume", real.SessionResumeAsync),
            ("session.list", real.SessionListAsync),
            ("session.rename", real.SessionRenameAsync),
            ("session.delete", real.SessionDeleteAsync),
            ("session.clear", real.SessionClearAsync),
            ("session.compact", real.SessionCompactAsync),
            ("session.setCwd", real.SessionSetCwdAsync),

            ("state.snapshot", real.StateSnapshotAsync),
            ("state.setMode", real.StateSetModeAsync),
            ("state.setPersona", real.StateSetPersonaAsync),
            ("state.setProvider", real.StateSetProviderAsync),
            ("state.setMaxTurns", real.StateSetMaxTurnsAsync),
            ("state.setRetry", real.StateSetRetryAsync),

            ("settings.get", real.SettingsGetAsync),
            ("settings.saveModel", real.SettingsSaveModelAsync),
            ("settings.saveNetwork", real.SettingsSaveNetworkAsync),
            ("settings.probeModels", real.SettingsProbeModelsAsync),
            ("settings.saveExtraHeaders", real.SettingsSaveExtraHeadersAsync),
            ("settings.mcp.get", real.SettingsMcpGetAsync),
            ("settings.mcp.save", real.SettingsMcpSaveAsync),

            ("asset.skills.list", real.AssetSkillsListAsync),
            ("asset.skills.get", real.AssetSkillsGetAsync),
            ("asset.skills.save", real.AssetSkillsSaveAsync),
            ("asset.skills.delete", real.AssetSkillsDeleteAsync),
            ("asset.skills.run", real.AssetSkillsRunAsync),
            ("asset.themes.list", real.AssetThemesListAsync),
            ("asset.themes.get", real.AssetThemesGetAsync),
            ("asset.personas.list", real.AssetPersonasListAsync),
            ("asset.personas.get", real.AssetPersonasGetAsync),
            ("asset.personas.save", real.AssetPersonasSaveAsync),
            ("asset.memory.list", real.AssetMemoryListAsync),
            ("asset.memory.get", real.AssetMemoryGetAsync),
            ("asset.memory.save", real.AssetMemorySaveAsync),
            ("asset.memory.delete", real.AssetMemoryDeleteAsync),

            ("tool.list", real.ToolListAsync),
            ("tool.undoList", real.ToolUndoListAsync),
            ("tool.undoRollback", real.ToolUndoRollbackAsync),
            ("tool.mcpStatus", real.ToolMcpStatusAsync),

            ("fs.listDir", real.FsListDirAsync),
            ("fs.completePaths", real.FsCompletePathsAsync),
            ("fs.validatePath", real.FsValidatePathAsync),

            ("update.check", real.UpdateCheckAsync),
            ("update.install", real.UpdateInstallAsync));

        return new AssembledEngine(router, state, bus, session, store, tools);
    }
}
