namespace Host.Desktop.Core.Models;

/// <summary>
/// 固定在 %LocalAppData%\HaloAgent\bootstrap.json 的引导指针配置。
/// </summary>
public sealed class StorageBootstrapConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string DataRootPath { get; set; } = @"C:\HaloAgentData";
    public string? PreviousDataRootPath { get; set; }
    public string LastModifiedUtc { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}

/// <summary>
/// 位于 &lt;DataRootPath&gt;\config\app-settings.json 的应用全局基础配置。
/// </summary>
public sealed class AppSettingsConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>P007：压缩模式开关：legacy（旧重写）/ stub（默认 stub；老配置缺该键时走默认值）。</summary>
    public string CompactMode { get; set; } = "stub";
    public string Theme { get; set; } = "dark";
    public string PreferredShell { get; set; } = "cmd";
    public int LogRetentionDays { get; set; } = 14;
    public string LastUpdatedUtc { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}

/// <summary>
/// 位于 &lt;DataRootPath&gt;\config\ui-state.json 的 UI 活跃状态（上次离开时的工作区+会话，只恢复到 main）。
/// 独立文件：避免与 app-settings.json 多写者读-改-写覆盖；单实例互斥，无跨进程锁需求。
/// </summary>
public sealed class UiActiveState
{
    public int SchemaVersion { get; set; } = 1;
    public string? LastWorkspaceId { get; set; }
    public string? LastSessionId { get; set; }
    public string LastActiveAtUtc { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}

/// <summary>UI 活跃状态桥接 DTO（只含工作区+会话 ID）。</summary>
public sealed class UiActiveStateDto
{
    public string? WorkspaceId { get; set; }
    public string? SessionId { get; set; }
}

/// <summary>
/// 位于 &lt;DataRootPath&gt;\config\appearance.json 的前端外观真相源。
/// 独立文件：dev(http://localhost:5173) 与 release(app://local) 的 localStorage 按 origin 隔离，
/// 故外观配置不再以浏览器 localStorage 为真相源，统一落到后端数据根，两种模式共享。
/// 默认值与前端 shared/ui/appearance.ts 的 DEFAULT_APPEARANCE 严格对齐。
/// </summary>
public sealed class AppearanceState
{
    public int SchemaVersion { get; set; } = 1;
    public string ThemeMode { get; set; } = "dark";
    public string ThemeColor { get; set; } = "#3b82f6";
    public int FontSize { get; set; } = 13;
    public string ChineseFont { get; set; } = "Microsoft YaHei UI";
    public string EnglishFont { get; set; } = "Segoe UI";
    public string MonoFont { get; set; } = "Consolas";
    public string ChatWidthMode { get; set; } = "centered";
    public string SendKeyMode { get; set; } = "enter";
    public string LastUpdatedUtc { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}

/// <summary>
/// 宿主系统运行环境与诊断信息 DTO。
/// </summary>
public sealed class SystemRuntimeInfo
{
    public string OsDescription { get; init; } = string.Empty;
    public string OsVersionString { get; init; } = string.Empty;
    public bool IsWin7 { get; init; }
    public string Architecture { get; init; } = string.Empty;
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string DotnetVersion { get; init; } = string.Empty;
    public string CefSharpVersion { get; init; } = string.Empty;
    public string CurrentDataRoot { get; init; } = string.Empty;
    public string BootstrapFilePath { get; init; } = string.Empty;
    public string LogsDirectory { get; init; } = string.Empty;
    public bool GpuAccelerationDisabled { get; init; }
    public string TlsProtocols { get; init; } = string.Empty;
}

/// <summary>
/// 存储路径与日志监控面板状态 DTO。
/// </summary>
public sealed class StorageConfigInfo
{
    public string DataRootPath { get; init; } = string.Empty;
    public string DefaultPath { get; init; } = @"C:\HaloAgentData";
    public string FallbackPath { get; init; } = string.Empty;
    public string BootstrapFilePath { get; init; } = string.Empty;
    public string? PreviousDataRootPath { get; init; }
    public string LastModifiedUtc { get; init; } = string.Empty;
    public bool IsUsingFallback { get; init; }
    public bool IsWritable { get; init; }
    public long DiskFreeSpaceMb { get; init; }
    public long DiskTotalSpaceMb { get; init; }
    public int LogRetentionDays { get; init; } = 14;
    public List<LogChannelStatus> LogChannels { get; init; } = new();
}

/// <summary>
/// 单个日志通道文件状态。
/// </summary>
public sealed class LogChannelStatus
{
    public string Channel { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool Exists { get; init; }
    public string? LastWriteTimeUtc { get; init; }
}

/// <summary>
/// 目标存储路径合法性与权限预检结果。
/// </summary>
public sealed class StorageValidationResult
{
    public bool IsValid { get; init; }
    public string NormalizedPath { get; init; } = string.Empty;
    public string? ReasonCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public long AvailableFreeSpaceMb { get; init; }

    /// <summary>完整迁移预估所需空间（MB），供前端在切换前提示空间风险。</summary>
    public long RequiredSpaceMb { get; init; }

    /// <summary>目标目录是否已有内容（完整迁移可能覆盖同名文件）。</summary>
    public bool TargetNonEmpty { get; init; }

    /// <summary>细分风险码（如空间不足以迁移），前端据此禁用切换并给出原因。</summary>
    public string? RiskCode { get; init; }
}

/// <summary>单个受管子目录的存储占用明细。SizeBytes 始终是物理字节总和。</summary>
public sealed class StorageBreakdownItem
{
    public string Name { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }

    /// <summary>
    /// 仅 sessions 行填充：每个工作区主库的页级明细。不存在时为 null，老前端忽略。
    /// </summary>
    public List<WorkspaceDatabaseStorageDetail>? DatabaseDetails { get; init; }
}

/// <summary>
/// 单个工作区历史库的页级存储明细：逻辑占用与物理文件的双口径对照。
/// 页统计只针对主库文件，不含备份与会话私有目录。
/// </summary>
public sealed class WorkspaceDatabaseStorageDetail
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string DatabasePath { get; init; } = string.Empty;
    public long PhysicalBytes { get; init; }
    public long PageSizeBytes { get; init; }
    public long PageCount { get; init; }
    public long FreePageCount { get; init; }
    public long UsedPageBytes { get; init; }
    public long ReclaimablePageBytes { get; init; }
    public long BackupBytes { get; init; }
    public long WalBytes { get; init; }
    public long ShmBytes { get; init; }
    public long TempBytes { get; init; }
    public bool Measured { get; init; }
    public string? Warning { get; init; }
    public string MeasuredAtUtc { get; init; } = string.Empty;
}

/// <summary>工作区历史库手动压实结果：权威写失败绝不报成功，辅助阶段只记告警。</summary>
public sealed class WorkspaceCompactionResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public long BeforeBytes { get; init; }
    public long AfterBytes { get; init; }
    public long ReclaimedBytes => Math.Max(0, BeforeBytes - AfterBytes);
    public bool WalCheckpointed { get; init; }
    public string? Warning { get; init; }
}

/// <summary>工作区历史库备份清理结果：只删备份与过期临时文件，主库与 WAL/SHM 永不触碰。</summary>
public sealed class WorkspaceBackupCleanupResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public long FreedBytes { get; init; }
    public bool BackupExisted { get; init; }
    public string? Warning { get; init; }
}

/// <summary>数据根目录按子目录聚合的存储分布。</summary>
public sealed class StorageBreakdown
{
    public string RootPath { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public List<StorageBreakdownItem> Items { get; init; } = new();
}

/// <summary>持久化的存储事件记录（与 jsonl 每行一一对应）。</summary>
public sealed class StorageEventDto
{
    public string Type { get; init; } = string.Empty;
    public object? Payload { get; init; }
    public long Timestamp { get; init; }
}

/// <summary>
/// 存储路径修改与数据迁移结果。
/// </summary>
public sealed class StorageMigrationResult
{
    public bool Success { get; init; }
    public string OldDataRootPath { get; init; } = string.Empty;
    public string NewDataRootPath { get; init; } = string.Empty;
    public bool MigratedData { get; init; }
    public int MigratedFileCount { get; init; }
    public long MigratedTotalBytes { get; init; }
    public string Message { get; init; } = string.Empty;
}
