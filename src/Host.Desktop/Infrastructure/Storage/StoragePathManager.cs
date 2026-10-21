using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 两级引导存储中枢（Two-Tier Storage Bootstrap）：
/// 1. 固定引导指针：%LocalAppData%\HaloAgent\bootstrap.json
/// 2. 默认数据根目录：C:\HaloAgentData（无写权限时平滑降级到 %LocalAppData%\HaloAgent\Data）
/// 3. 支持合法性预检、一键迁移已有历史数据与日志或仅切换新目录。
/// </summary>
public sealed partial class StoragePathManager
{
    public const string DefaultCDriveDataRoot = @"C:\HaloAgentData";
    public const long MinRequiredFreeSpaceBytes = 200L * 1024L * 1024L; // 200 MB

    private static readonly string[] ManagedSubDirectories =
    {
        "config",
        "workspaces",
        "sessions",
        "logs",
        "cef-cache",
        "skills",
        "memory",
        "metrics"
    };

    private static readonly string[] MigratableSubDirectories =
    {
        "config",
        "workspaces",
        "sessions",
        "logs",
        "skills",
        "memory",
        "metrics"
    };

    private readonly object _stateLock = new();
    private string _currentDataRoot;
    private string? _previousDataRoot;
    private string _lastModifiedUtc;
    private bool _isUsingFallback;

    public string BootstrapFilePath { get; }
    public string DefaultDataRootPath { get; }
    public string FallbackDataRootPath { get; }

    public string CurrentDataRoot
    {
        get
        {
            lock (_stateLock)
                return _currentDataRoot;
        }
    }

    public string? PreviousDataRoot
    {
        get
        {
            lock (_stateLock)
                return _previousDataRoot;
        }
    }

    public string LastModifiedUtc
    {
        get
        {
            lock (_stateLock)
                return _lastModifiedUtc;
        }
    }

    public bool IsUsingFallback
    {
        get
        {
            lock (_stateLock)
                return _isUsingFallback;
        }
    }

    public string ConfigDirectory => Path.Combine(CurrentDataRoot, "config");
    public string WorkspacesDirectory => Path.Combine(CurrentDataRoot, "workspaces");
    public string SessionsDirectory => Path.Combine(CurrentDataRoot, "sessions");
    public string LogsDirectory => Path.Combine(CurrentDataRoot, "logs");
    public string CefCacheDirectory => Path.Combine(CurrentDataRoot, "cef-cache");

    /// <summary>need09-01：Global Skill 域根目录（随数据根迁移）。</summary>
    public string GlobalSkillsDirectory => Path.Combine(CurrentDataRoot, "skills");

    /// <summary>need09-01：Global Memory 域根目录（随数据根迁移）。</summary>
    public string GlobalMemoryDirectory => Path.Combine(CurrentDataRoot, "memory");

    /// <summary>need12：用量库目录（随数据根迁移，含 usage.db + WAL）。</summary>
    public string MetricsDirectory => Path.Combine(CurrentDataRoot, "metrics");

    /// <summary>need09-01：迁移白名单（含 skills/memory，供测试与迁移复用）。</summary>
    public static IReadOnlyList<string> MigratableSubDirectoryNames => MigratableSubDirectories;

    /// <summary>need09-01：标准子目录清单（含 skills/memory）。</summary>
    public static IReadOnlyList<string> ManagedSubDirectoryNames => ManagedSubDirectories;
    public string AppSettingsFilePath => Path.Combine(ConfigDirectory, "app-settings.json");

    /// <summary>UI 活跃状态文件（独立于 app-settings，避免多写者读-改-写覆盖；随数据根迁移）。</summary>
    public string UiStateFilePath => Path.Combine(ConfigDirectory, "ui-state.json");

    private StoragePathManager(
        string bootstrapFilePath,
        string defaultDataRootPath,
        string fallbackDataRootPath,
        string activeDataRoot,
        string? previousDataRoot,
        string lastModifiedUtc,
        bool isUsingFallback
    )
    {
        BootstrapFilePath = bootstrapFilePath;
        DefaultDataRootPath = defaultDataRootPath;
        FallbackDataRootPath = fallbackDataRootPath;
        _currentDataRoot = activeDataRoot;
        _previousDataRoot = previousDataRoot;
        _lastModifiedUtc = lastModifiedUtc;
        _isUsingFallback = isUsingFallback;
    }

    /// <summary>
    /// 最近一次 Initialize 调用的候选目录决策链（成功/失败均记录，供启动追踪与冒烟诊断使用）。
    /// </summary>
    public static string LastInitReport { get; private set; } = string.Empty;

    /// <summary>
    /// 启动时初始化两级引导存储结构，确保 bootstrap.json 与标准子目录树落盘。
    /// </summary>
    public static StoragePathManager Initialize(
        string? customBootstrapFilePath = null,
        string? customDefaultDataRoot = null,
        string? customFallbackDataRoot = null
    )
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        var haloLocalAppDataDir = Path.Combine(localAppData, "HaloAgent");

        var envBootstrap = Environment.GetEnvironmentVariable("HALO_BOOTSTRAP_PATH");
        var bootstrapPath = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customBootstrapFilePath)
                ? customBootstrapFilePath
                : !string.IsNullOrWhiteSpace(envBootstrap)
                    ? envBootstrap
                    : Path.Combine(haloLocalAppDataDir, "bootstrap.json")
        );

        var defaultRoot = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customDefaultDataRoot)
                ? customDefaultDataRoot
                : DefaultCDriveDataRoot
        );

        var fallbackRoot = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customFallbackDataRoot)
                ? customFallbackDataRoot
                : Path.Combine(haloLocalAppDataDir, "Data")
        );

        Directory.CreateDirectory(Path.GetDirectoryName(bootstrapPath)!);

        var existingBootstrap = AtomicFileWriter.ReadJsonOrDefault<StorageBootstrapConfig>(
            bootstrapPath
        );
        var preferredPath = !string.IsNullOrWhiteSpace(existingBootstrap?.DataRootPath)
            ? existingBootstrap!.DataRootPath
            : defaultRoot;

        string activePath;
        var usingFallback = false;
        var attempts = new List<string>
        {
            $"bootstrap={bootstrapPath}",
            $"preferred={preferredPath}",
            $"default={defaultRoot}",
            $"fallback={fallbackRoot}",
        };

        if (TryEnsureDirectoryWritable(preferredPath, ".write_test", out var preferredError))
        {
            activePath = Path.GetFullPath(preferredPath);
            attempts.Add("preferred: OK");
        }
        else
        {
            attempts.Add($"preferred: FAIL {preferredError}");
            string? defaultError = null;
            if (
                !string.Equals(preferredPath, defaultRoot, StringComparison.OrdinalIgnoreCase)
                && TryEnsureDirectoryWritable(defaultRoot, ".write_test", out defaultError)
            )
            {
                activePath = defaultRoot;
                attempts.Add("default: OK");
            }
            else
            {
                if (!string.Equals(preferredPath, defaultRoot, StringComparison.OrdinalIgnoreCase))
                {
                    attempts.Add($"default: FAIL {defaultError}");
                }
                // 多级兜底：首选 fallbackRoot；若其亦不可写（如 LocalAppData 被 ACL/杀软锁定），
                // 最后退守 %TEMP%\HaloAgent\Data。所有候选均失败时才抛出带明细的异常，
                // 绝不允许裸 Directory.CreateDirectory 直接崩掉启动流程。
                var candidates = new[]
                {
                    fallbackRoot,
                    Path.Combine(Path.GetTempPath(), "HaloAgent", "Data")
                };
                string? resolvedPath = null;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        if (
                            TryEnsureDirectoryWritable(candidate, ".write_test", out var probeError)
                        )
                        {
                            resolvedPath = candidate;
                            attempts.Add($"fallback {candidate}: OK");
                            break;
                        }
                        attempts.Add($"fallback {candidate}: FAIL {probeError}");
                    }
                    catch (Exception ex)
                    {
                        attempts.Add($"fallback {candidate}: EX {ex.Message}");
                    }
                }
                if (resolvedPath == null)
                {
                    LastInitReport = string.Join(" | ", attempts);
                    throw new IOException(
                        "无法初始化任何可写数据目录（已尝试 fallback 与 TEMP 兜底）。"
                            + "请检查杀毒软件/权限后重试，或设置环境变量 HALO_BOOTSTRAP_PATH 指向可写位置。"
                            + "明细: "
                            + LastInitReport
                    );
                }
                activePath = resolvedPath;
                usingFallback = true;
            }
        }
        LastInitReport = string.Join(" | ", attempts) + $" => active={activePath}";

        EnsureStandardSubdirectories(activePath);

        var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var previousRoot = existingBootstrap?.PreviousDataRootPath;
        var lastModifiedUtc = existingBootstrap?.LastModifiedUtc ?? nowUtc;

        var bootstrapToSave = new StorageBootstrapConfig
        {
            SchemaVersion = 1,
            DataRootPath = activePath,
            PreviousDataRootPath = previousRoot,
            LastModifiedUtc = lastModifiedUtc
        };

        AtomicFileWriter.WriteJsonAtomic(bootstrapPath, bootstrapToSave);

        var manager = new StoragePathManager(
            bootstrapPath,
            defaultRoot,
            fallbackRoot,
            activePath,
            previousRoot,
            lastModifiedUtc,
            usingFallback
        );

        manager.EnsureDefaultAppSettings();
        return manager;
    }

    public AppSettingsConfig LoadAppSettings()
    {
        var settings = AtomicFileWriter.ReadJsonOrDefault<AppSettingsConfig>(AppSettingsFilePath);
        if (settings != null)
        {
            return settings;
        }

        var defaults = new AppSettingsConfig();
        EnsureDefaultAppSettings();
        return defaults;
    }

    public async Task SaveAppSettingsAsync(
        AppSettingsConfig settings,
        CancellationToken ct = default
    )
    {
        settings.LastUpdatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        await AtomicFileWriter
            .WriteJsonAtomicAsync(AppSettingsFilePath, settings, ct)
            .ConfigureAwait(false);
    }

    private void EnsureDefaultAppSettings()
    {
        if (!File.Exists(AppSettingsFilePath))
        {
            var defaultSettings = new AppSettingsConfig();
            AtomicFileWriter.WriteJsonAtomic(AppSettingsFilePath, defaultSettings);
        }
    }

    /// <summary>读取 UI 活跃状态（无文件/空/坏 JSON 一律回默认空状态，永不抛）。</summary>
    public UiActiveState LoadUiState()
    {
        var state = AtomicFileWriter.ReadJsonOrDefault<UiActiveState>(UiStateFilePath);
        return state ?? new UiActiveState();
    }

    /// <summary>保存 UI 活跃状态（切换即写，原子落盘；单实例互斥，无锁需求）。</summary>
    public async Task SaveUiStateAsync(UiActiveState state, CancellationToken ct = default)
    {
        state.LastActiveAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        await AtomicFileWriter
            .WriteJsonAtomicAsync(UiStateFilePath, state, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 校验目标路径格式、系统目录保护、磁盘剩余空间（>= 200MB）及实际读写删除权限。
    /// </summary>
    public Task<StorageValidationResult> ValidateTargetRootPathAsync(
        string targetPath,
        CancellationToken ct = default
    )
    {
        return Task.Run(() => ValidateTargetRootPath(targetPath), ct);
    }

    public StorageValidationResult ValidateTargetRootPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new StorageValidationResult
            {
                IsValid = false,
                NormalizedPath = string.Empty,
                ReasonCode = "EMPTY_PATH",
                Message = "目标存储路径不能为空。"
            };
        }

        string fullPath;
        try
        {
            var rawFullPath = Path.GetFullPath(targetPath.Trim());
            if (!Path.IsPathFullyQualified(rawFullPath))
            {
                return new StorageValidationResult
                {
                    IsValid = false,
                    NormalizedPath = rawFullPath,
                    ReasonCode = "NOT_ABSOLUTE_PATH",
                    Message = "必须指定完整的绝对磁盘路径（例如 D:\\HaloAgentData）。"
                };
            }

            var pathRoot = Path.GetPathRoot(rawFullPath);
            if (
                !string.IsNullOrWhiteSpace(pathRoot)
                && string.Equals(
                    rawFullPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    ),
                    pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return new StorageValidationResult
                {
                    IsValid = false,
                    NormalizedPath = pathRoot,
                    ReasonCode = "DRIVE_ROOT_FORBIDDEN",
                    Message = "禁止直接将磁盘根目录设为数据目录，请选择具体子文件夹（如 C:\\HaloAgentData）。"
                };
            }

            fullPath = rawFullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
        }
        catch (Exception ex)
        {
            return new StorageValidationResult
            {
                IsValid = false,
                NormalizedPath = targetPath,
                ReasonCode = "INVALID_PATH_FORMAT",
                Message = $"路径格式非法：{ex.Message}"
            };
        }

        var rootDir = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(rootDir))
        {
            return new StorageValidationResult
            {
                IsValid = false,
                NormalizedPath = fullPath,
                ReasonCode = "DRIVE_ROOT_FORBIDDEN",
                Message = "禁止直接将磁盘根目录设为数据目录，请选择具体子文件夹（如 C:\\HaloAgentData）。"
            };
        }

        if (IsProtectedSystemDirectory(fullPath))
        {
            return new StorageValidationResult
            {
                IsValid = false,
                NormalizedPath = fullPath,
                ReasonCode = "SYSTEM_PROTECTED_PATH",
                Message = "禁止将 Windows 系统目录或 Program Files 设为数据存储目录。"
            };
        }

        long availableFreeSpaceMb = 0;
        try
        {
            var drive = new DriveInfo(rootDir);
            if (!drive.IsReady)
            {
                return new StorageValidationResult
                {
                    IsValid = false,
                    NormalizedPath = fullPath,
                    ReasonCode = "DRIVE_NOT_READY",
                    Message = $"目标磁盘 {rootDir} 未就绪。"
                };
            }

            availableFreeSpaceMb = drive.AvailableFreeSpace / (1024 * 1024);
            if (drive.AvailableFreeSpace < MinRequiredFreeSpaceBytes)
            {
                return new StorageValidationResult
                {
                    IsValid = false,
                    NormalizedPath = fullPath,
                    ReasonCode = "INSUFFICIENT_DISK_SPACE",
                    AvailableFreeSpaceMb = availableFreeSpaceMb,
                    Message = $"目标磁盘可用空间不足 200 MB（当前剩余 {availableFreeSpaceMb} MB）。"
                };
            }
        }
        catch (Exception ex)
        {
            return new StorageValidationResult
            {
                IsValid = false,
                NormalizedPath = fullPath,
                ReasonCode = "DRIVE_CHECK_FAILED",
                Message = $"无法检查目标磁盘空间：{ex.Message}"
            };
        }

        if (!TryEnsureDirectoryWritable(fullPath, ".halo_write_probe", out var writeError))
        {
            return new StorageValidationResult
            {
                IsValid = false,
                NormalizedPath = fullPath,
                ReasonCode = "WRITE_PERMISSION_DENIED",
                AvailableFreeSpaceMb = availableFreeSpaceMb,
                Message = $"当前账户对目标目录无读写或删除权限：{writeError}"
            };
        }

        return new StorageValidationResult
        {
            IsValid = true,
            NormalizedPath = fullPath,
            AvailableFreeSpaceMb = availableFreeSpaceMb,
            Message = "目标路径校验通过，具备完整读写权限。"
        };
    }
}
