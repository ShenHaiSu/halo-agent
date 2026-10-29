namespace HaloAgent.Engine.Abstractions.Types;

/// <summary>
/// 全量状态快照。前端拿到它就能完整重建界面。
/// 用全量而非增量：增量恢复在丢帧后必然不一致，且需要前端实现事件重放器；
/// 全量快照一次几十~几百 KB，管道带宽完全够；恢复逻辑只是清空 store、塞入快照。
/// </summary>
public sealed record StateSnapshot
{
    /// <summary>帧协议版本。</summary>
    public int Protocol { get; init; } = 1;
    /// <summary>引擎进程信息。</summary>
    public EngineInfo Engine { get; init; } = new();
    /// <summary>会话状态。</summary>
    public SessionState Session { get; init; } = new();
    /// <summary>智能体状态。</summary>
    public AgentState Agent { get; init; } = new();
    /// <summary>用量统计。</summary>
    public UsageState Usage { get; init; } = new();
    /// <summary>排队中的用户输入。</summary>
    public IReadOnlyList<string> Queue { get; init; } = Array.Empty<string>();
    /// <summary>待回答的询问（同一时刻最多一个）。</summary>
    public PendingAsk? PendingAsk { get; init; }
    /// <summary>全部消息。</summary>
    public IReadOnlyList<MessageView> Messages { get; init; } = Array.Empty<MessageView>();
    /// <summary>备注（info/warn/error）。</summary>
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();
    /// <summary>更新状态。</summary>
    public UpdateState Update { get; init; } = new();
}

/// <summary>引擎进程信息。</summary>
public sealed record EngineInfo
{
    /// <summary>引擎版本。</summary>
    public string Version { get; init; } = "";
    /// <summary>进程 id。</summary>
    public int Pid { get; init; }
    /// <summary>启动时刻（毫秒时间戳）。</summary>
    public long StartedAtMs { get; init; }
    /// <summary>操作系统版本字符串。</summary>
    public string OsVersion { get; init; } = "";
    /// <summary>是否为 Win7。</summary>
    public bool IsWin7 { get; init; }
}

/// <summary>会话状态。</summary>
public sealed record SessionState
{
    /// <summary>会话 id。</summary>
    public string Id { get; init; } = "";
    /// <summary>工作目录。</summary>
    public string Cwd { get; init; } = "";
    /// <summary>历史文件路径（可为空）。</summary>
    public string? HistoryPath { get; init; }
}

/// <summary>智能体状态（P10：active 指针 + 模型能力位）。</summary>
public sealed record AgentState
{
    /// <summary>是否有回合正在执行。</summary>
    public bool Busy { get; init; }
    /// <summary>当前回合 id（空闲时为 null）。</summary>
    public string? TurnId { get; init; }
    /// <summary>权限模式。</summary>
    public PermissionMode Mode { get; init; } = PermissionMode.Ask;
    /// <summary>权限模式界面标签。</summary>
    public string ModeLabel { get; init; } = "询问";
    /// <summary>人格名。</summary>
    public string Persona { get; init; } = "";
    /// <summary>服务商注册名（provider id）。</summary>
    public string Provider { get; init; } = "";
    /// <summary>服务商别称（回落 id）。</summary>
    public string ProviderDisplay { get; init; } = "";
    /// <summary>模型实际请求名。</summary>
    public string Model { get; init; } = "";
    /// <summary>模型别称（回落 id）。</summary>
    public string ModelDisplay { get; init; } = "";
    /// <summary>协议：chat 可用，responses 预留。</summary>
    public string Protocol { get; init; } = "chat";
    /// <summary>服务地址。</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>是否已配置 API Key。</summary>
    public bool ApiKeySet { get; init; }
    /// <summary>上下文窗口（可为空=未知）。</summary>
    public int? ContextWindow { get; init; }
    /// <summary>输出 token 最大值（可为空=不限制）。</summary>
    public int? MaxTokens { get; init; }
    /// <summary>是否接受图片。</summary>
    public bool AcceptsImages { get; init; }
    /// <summary>回放历史是否携带 reasoning_content。</summary>
    public bool IncludeReasoning { get; init; } = true;
    /// <summary>本次模型请求是否走代理（脱敏 bool）。</summary>
    public bool ProxyInUse { get; init; }
    /// <summary>最大轮数。</summary>
    public int MaxTurns { get; init; }
    /// <summary>重试次数上限。</summary>
    public int Retry { get; init; }
    /// <summary>是否已完成模型配置。</summary>
    public bool Configured { get; init; }
    /// <summary>轮次预算。</summary>
    public RoundState Round { get; init; } = new();
}

/// <summary>轮次预算。</summary>
public sealed record RoundState
{
    /// <summary>已用轮数。</summary>
    public int Used { get; init; }
    /// <summary>轮数上限。</summary>
    public int Limit { get; init; }
}

/// <summary>用量统计。</summary>
public sealed record UsageState
{
    /// <summary>累计输入 token。</summary>
    public long InputTotal { get; init; }
    /// <summary>累计输出 token。</summary>
    public long OutputTotal { get; init; }
    /// <summary>上次 prompt token 数（可为空）。</summary>
    public long? LastPromptTokens { get; init; }
}

/// <summary>更新状态。</summary>
public sealed record UpdateState
{
    /// <summary>是否有可用更新。</summary>
    public bool Available { get; init; }
    /// <summary>最新版本号（无更新时为 null）。</summary>
    public string? LatestVersion { get; init; }
}
