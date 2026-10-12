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
    public string Theme { get; set; } = "dark";
    public string PreferredShell { get; set; } = "cmd";
    public int LogRetentionDays { get; set; } = 14;
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
