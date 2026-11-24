using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 工作区级会话仓储统一契约：摘要 / 过程 / 工具结果三层懒加载读模型，
/// 以及回合追加 / 工具结果追加 / 截断 / 重写四种写模型，
/// 外加会话注册 / 删除 / 工作区备份三个生命周期操作。
/// 本阶段仅定义接口，不接管任何生产读写链路；实现由后续阶段落地。
/// 读侧不提供全量消息接口，调用方按摘要先行、按需展开的顺序消费。
/// </summary>
public interface IWorkspaceSessionStore
{
    /// <summary>摘要层：首屏一次返回每回合的用户全文与末轮正文，无需额外按块请求。</summary>
    Task<List<TurnSummary>> GetSummaryAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );

    /// <summary>锚点投影专用：只读消息 id / role / kind，不返回正文与图片字节。</summary>
    Task<List<HistoryMessageRef>> GetMessageRefsAsync(
        string workspaceId,
        string sessionId,
        CancellationToken ct = default
    );

    /// <summary>过程层：展开指定回合时按需取块标识与预览，完整正文按块二次加载。</summary>
    Task<List<TimelineBlockContent>> GetBlockIdsAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        CancellationToken ct = default
    );

    /// <summary>工具结果层：按回合与调用标识单次取回工具执行结果。</summary>
    Task<TimelineToolResult?> GetToolResultAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        string callId,
        CancellationToken ct = default
    );

    /// <summary>标题链专用：按回合取指定回合的用户全文。</summary>
    Task<string> GetUserContentAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        CancellationToken ct = default
    );

    /// <summary>写模型：原子追加一整轮（消息行、展示块、轮次摘要同一事务落盘）。</summary>
    Task<long> AppendTurnAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> messages,
        IReadOnlyList<TimelineBlock> blocks,
        TurnSummary turn,
        CancellationToken ct = default
    );

    /// <summary>写模型：回合进行中追加单个工具结果块，并累加轮次计数。</summary>
    Task AppendToolResultAsync(
        string workspaceId,
        string sessionId,
        string turnId,
        TimelineBlock resultBlock,
        bool isFailed,
        CancellationToken ct = default
    );

    /// <summary>写模型：从锚点回合起截断其后全部轮次（含展示块与轮次摘要）。</summary>
    Task TruncateFromTurnAsync(
        string workspaceId,
        string sessionId,
        string anchorTurnId,
        CancellationToken ct = default
    );

    /// <summary>写模型：以保留尾部消息集合重写会话消息区（截断 / 删除 / 压缩后同步用）。</summary>
    Task RewriteSessionAsync(
        string workspaceId,
        string sessionId,
        IReadOnlyList<UnifiedChatMessage> tail,
        CancellationToken ct = default
    );

    /// <summary>会话生命周期：在工作区库注册会话路由（格式版本与会话语义仅登记，不重复存可变元数据）。</summary>
    Task RegisterSessionAsync(
        string workspaceId,
        string sessionId,
        int storageFormat,
        string sessionKind,
        CancellationToken ct = default
    );

    /// <summary>会话生命周期：删除工作区库中指定会话分区的全部行。</summary>
    Task DeleteSessionAsync(string workspaceId, string sessionId, CancellationToken ct = default);

    /// <summary>会话生命周期：对工作区库做在线一致性快照，返回快照位置信息。</summary>
    Task<WorkspaceBackupInfo> BackupWorkspaceAsync(
        string workspaceId,
        CancellationToken ct = default
    );
}

/// <summary>
/// 工作区库在线备份快照的位置信息（快照文件路径与创建时间）。
/// </summary>
public sealed class WorkspaceBackupInfo
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string BackupPath { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 存储格式不受支持：库结构版本或会话格式版本与当前实现不一致，
/// 且按约束不做静默兼容与迁移，调用方应中止打开并提示用户。
/// </summary>
public sealed class UnsupportedFormatException : InvalidOperationException
{
    public UnsupportedFormatException(string message)
        : base(message) { }
}

/// <summary>
/// 老会话格式：元数据未标注存储格式版本，界面应列表可见但拒绝打开，
/// 并向用户提示旧格式不支持，不触发任何底层读取。
/// </summary>
public sealed class LegacySessionFormatException : InvalidOperationException
{
    public LegacySessionFormatException(string message)
        : base(message) { }
}
