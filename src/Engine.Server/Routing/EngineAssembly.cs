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
/// P10 装配：按 active 装配；失败（无 active / provider 空 / model 缺）则引擎仍启动但 Configured=false。
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

        // 1) 读配置 + 解析 active（失败则未配置态，不抛）
        var cfg = ConfigStore.Load();
        ResolvedActiveModel? resolved = null;
        try { resolved = ConfigStore.Resolve(cfg); }
        catch { resolved = null; }

        // 种子：新文件首次创建时写入内置种子
        if (!File.Exists(Paths.ConfigFile) || cfg.Providers.Count == 0 && resolved is null)
        {
            // 不自动写种子覆盖用户空配置；仅在文件不存在时由首次 saveProviders 创建。
        }

        var llmProto = resolved is not null && string.Equals(resolved.Protocol, "responses", StringComparison.OrdinalIgnoreCase)
            ? LlmProtocol.Responses : LlmProtocol.Chat;
        var llm = new OpenAiCompatibleClient(new LlmConfig
        {
            BaseUrl = resolved?.BaseUrl ?? "http://localhost:11434/v1",
            Model = resolved?.ModelId ?? "",
            ApiKey = resolved?.ApiKey,
            ProxyUrl = resolved?.ProxyUrl,
            Headers = resolved?.Headers ?? Array.Empty<KeyValuePair<string, string>>(),
            Protocol = llmProto,
            MaxTokens = resolved?.MaxTokens,
            AcceptsImages = resolved?.AcceptsImages ?? false,
            IncludeReasoningHistory = resolved?.IncludeReasoningHistory ?? true,
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
            Paths.DataDir, Environment.CurrentDirectory, PermissionMode.Ask, resolved?.Persona ?? "",
            resolved?.MaxTurns ?? 200, resolved?.Retry ?? 0,
            askInteractive: true);
        if (resolved is not null)
        {
            try { session.ReconfigureActive(resolved); }
            catch (NotSupportedException) { session.ClearActive(); }
        }
        else
        {
            session.SetGlobalProxy(ConfigStore.GlobalProxyUrl(cfg));
        }

        // 5) 状态（给 stub 的 system.* 用）+ 同步已解析的配置
        var router = new MethodRouter();
        var state = new EngineState(Paths.DataDir, router)
        {
            Provider = resolved?.ProviderId ?? "",
            ProviderDisplay = resolved?.ProviderDisplay ?? "",
            Model = resolved?.ModelId ?? "",
            ModelDisplay = resolved?.ModelDisplay ?? "",
            Protocol = resolved?.Protocol ?? "chat",
            BaseUrl = resolved?.BaseUrl ?? "",
            ApiKeySet = !string.IsNullOrEmpty(resolved?.ApiKey),
            ContextWindow = resolved?.ContextWindow,
            MaxTokens = resolved?.MaxTokens,
            AcceptsImages = resolved?.AcceptsImages ?? false,
            IncludeReasoning = resolved?.IncludeReasoningHistory ?? true,
            ProxyInUse = resolved?.ProxyInUse ?? false,
            Configured = resolved is not null,
            MaxTurns = resolved?.MaxTurns ?? 200,
            Retry = resolved?.Retry ?? 0,
            Persona = resolved?.Persona ?? "",
        };

        // 6) 注册路由（P10 新契约；旧方法删除）
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
            ("state.setMaxTurns", real.StateSetMaxTurnsAsync),
            ("state.setRetry", real.StateSetRetryAsync),

            ("settings.get", real.SettingsGetAsync),
            ("settings.saveProviders", real.SettingsSaveProvidersAsync),
            ("settings.setActive", real.SettingsSetActiveAsync),
            ("settings.saveNetwork", real.SettingsSaveNetworkAsync),
            ("settings.probeModels", real.SettingsProbeModelsAsync),
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
