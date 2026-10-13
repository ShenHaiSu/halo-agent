using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 两级引导存储中枢（Two-Tier Storage Bootstrap）：
/// 1. 固定引导指针：%LocalAppData%\HaloAgent\bootstrap.json
/// 2. 默认数据根目录：C:\HaloAgentData（无写权限时平滑降级到 %LocalAppData%\HaloAgent\Data）
/// 3. 支持合法性预检、一键迁移已有历史数据与日志或仅切换新目录。
/// </summary>
public sealed class StoragePathManager
{
    public const string DefaultCDriveDataRoot = @"C:\HaloAgentData";
    public const long MinRequiredFreeSpaceBytes = 200L * 1024L * 1024L; // 200 MB

    private static readonly string[] ManagedSubDirectories =
    {
        "config",
        "workspaces",
        "sessions",
        "logs",
        "cef-cache"
    };

    private static readonly string[] MigratableSubDirectories =
    {
        "config",
        "workspaces",
        "sessions",
        "logs"
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
        get { lock (_stateLock) return _currentDataRoot; }
    }

    public string? PreviousDataRoot
    {
        get { lock (_stateLock) return _previousDataRoot; }
    }

    public string LastModifiedUtc
    {
        get { lock (_stateLock) return _lastModifiedUtc; }
    }

    public bool IsUsingFallback
    {
        get { lock (_stateLock) return _isUsingFallback; }
    }

    public string ConfigDirectory => Path.Combine(CurrentDataRoot, "config");
    public string WorkspacesDirectory => Path.Combine(CurrentDataRoot, "workspaces");
    public string SessionsDirectory => Path.Combine(CurrentDataRoot, "sessions");
    public string LogsDirectory => Path.Combine(CurrentDataRoot, "logs");
    public string CefCacheDirectory => Path.Combine(CurrentDataRoot, "cef-cache");
    public string AppSettingsFilePath => Path.Combine(ConfigDirectory, "app-settings.json");

    private StoragePathManager(
        string bootstrapFilePath,
        string defaultDataRootPath,
        string fallbackDataRootPath,
        string activeDataRoot,
        string? previousDataRoot,
        string lastModifiedUtc,
        bool isUsingFallback)
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
        string? customFallbackDataRoot = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var haloLocalAppDataDir = Path.Combine(localAppData, "HaloAgent");

        var envBootstrap = Environment.GetEnvironmentVariable("HALO_BOOTSTRAP_PATH");
        var bootstrapPath = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customBootstrapFilePath)
                ? customBootstrapFilePath
                : !string.IsNullOrWhiteSpace(envBootstrap)
                    ? envBootstrap
                    : Path.Combine(haloLocalAppDataDir, "bootstrap.json"));

        var defaultRoot = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customDefaultDataRoot)
                ? customDefaultDataRoot
                : DefaultCDriveDataRoot);

        var fallbackRoot = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customFallbackDataRoot)
                ? customFallbackDataRoot
                : Path.Combine(haloLocalAppDataDir, "Data"));

        Directory.CreateDirectory(Path.GetDirectoryName(bootstrapPath)!);

        var existingBootstrap = AtomicFileWriter.ReadJsonOrDefault<StorageBootstrapConfig>(bootstrapPath);
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
             if (!string.Equals(preferredPath, defaultRoot, StringComparison.OrdinalIgnoreCase) &&
                 TryEnsureDirectoryWritable(defaultRoot, ".write_test", out defaultError))
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
                         if (TryEnsureDirectoryWritable(candidate, ".write_test", out var probeError))
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
                         + "明细: " + LastInitReport);
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
            usingFallback);

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

    public async Task SaveAppSettingsAsync(AppSettingsConfig settings, CancellationToken ct = default)
    {
        settings.LastUpdatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        await AtomicFileWriter.WriteJsonAtomicAsync(AppSettingsFilePath, settings, ct).ConfigureAwait(false);
    }

    private void EnsureDefaultAppSettings()
    {
        if (!File.Exists(AppSettingsFilePath))
        {
            var defaultSettings = new AppSettingsConfig();
            AtomicFileWriter.WriteJsonAtomic(AppSettingsFilePath, defaultSettings);
        }
    }

    /// <summary>
    /// 校验目标路径格式、系统目录保护、磁盘剩余空间（>= 200MB）及实际读写删除权限。
    /// </summary>
    public Task<StorageValidationResult> ValidateTargetRootPathAsync(string targetPath, CancellationToken ct = default)
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
            if (!string.IsNullOrWhiteSpace(pathRoot) &&
                string.Equals(rawFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new StorageValidationResult
                {
                    IsValid = false,
                    NormalizedPath = pathRoot,
                    ReasonCode = "DRIVE_ROOT_FORBIDDEN",
                    Message = "禁止直接将磁盘根目录设为数据目录，请选择具体子文件夹（如 C:\\HaloAgentData）。"
                };
            }

            fullPath = rawFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    /// <summary>
    /// 修改存储根目录并按需迁移历史数据（config / workspaces / sessions / logs），原子更新 bootstrap.json。
    /// </summary>
    public async Task<StorageMigrationResult> ChangeDataRootAsync(
        string newTargetPath,
        bool migrateExistingData,
        Action<int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        var validation = await ValidateTargetRootPathAsync(newTargetPath, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return new StorageMigrationResult
            {
                Success = false,
                OldDataRootPath = CurrentDataRoot,
                NewDataRootPath = validation.NormalizedPath,
                MigratedData = migrateExistingData,
                Message = validation.Message
            };
        }

        var targetRoot = validation.NormalizedPath;
        var sourceRoot = CurrentDataRoot;

        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            EnsureStandardSubdirectories(targetRoot);
            return new StorageMigrationResult
            {
                Success = true,
                OldDataRootPath = sourceRoot,
                NewDataRootPath = targetRoot,
                MigratedData = migrateExistingData,
                MigratedFileCount = 0,
                MigratedTotalBytes = 0,
                Message = "目标路径与当前生效路径一致，无需迁移。"
            };
        }

        onProgress?.Invoke(5, "正在准备目标存储目录结构...");
        EnsureStandardSubdirectories(targetRoot);

        // 先将内存队列中的最新日志落盘，确保迁移包含最新日志
        await HaloLogger.FlushAsync().ConfigureAwait(false);

        var migratedCount = 0;
        long migratedBytes = 0;

        if (migrateExistingData && Directory.Exists(sourceRoot))
        {
            onProgress?.Invoke(15, "正在扫描待迁移配置、会话与日志文件...");
            var filesToCopy = CollectMigratableFiles(sourceRoot, targetRoot);
            var totalFiles = filesToCopy.Count;

            for (var i = 0; i < totalFiles; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (sourceFile, destFile) = filesToCopy[i];
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                await CopyFileSharedAsync(sourceFile, destFile, ct).ConfigureAwait(false);
                var fileInfo = new FileInfo(destFile);
                migratedCount++;
                migratedBytes += fileInfo.Exists ? fileInfo.Length : 0;

                var percent = 15 + (int)((i + 1) * 70.0 / Math.Max(1, totalFiles));
                onProgress?.Invoke(percent, $"正在迁移文件 ({i + 1}/{totalFiles}): {Path.GetFileName(sourceFile)}");
            }
        }

        onProgress?.Invoke(90, "正在原子更新引导指针文件 bootstrap.json...");
        var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var bootstrap = new StorageBootstrapConfig
        {
            SchemaVersion = 1,
            DataRootPath = targetRoot,
            PreviousDataRootPath = sourceRoot,
            LastModifiedUtc = nowUtc
        };

        await AtomicFileWriter.WriteJsonAtomicAsync(BootstrapFilePath, bootstrap, ct).ConfigureAwait(false);

        lock (_stateLock)
        {
            _previousDataRoot = sourceRoot;
            _currentDataRoot = targetRoot;
            _lastModifiedUtc = nowUtc;
            _isUsingFallback = string.Equals(targetRoot, FallbackDataRootPath, StringComparison.OrdinalIgnoreCase);
        }

        EnsureDefaultAppSettings();
        HaloLogger.SwitchLogsDirectory(LogsDirectory);
        HaloLogger.App.Info(
            $"Storage dataRootPath switched from '{sourceRoot}' to '{targetRoot}' (migrateExistingData={migrateExistingData}, files={migratedCount}, bytes={migratedBytes}).");
        await HaloLogger.FlushAsync().ConfigureAwait(false);

        onProgress?.Invoke(100, "存储路径切换完成。");

        return new StorageMigrationResult
        {
            Success = true,
            OldDataRootPath = sourceRoot,
            NewDataRootPath = targetRoot,
            MigratedData = migrateExistingData,
            MigratedFileCount = migratedCount,
            MigratedTotalBytes = migratedBytes,
            Message = migrateExistingData
                ? $"已成功迁移 {migratedCount} 个文件并切换至新路径：{targetRoot}"
                : $"已切换至全新存储路径：{targetRoot}"
        };
    }

    public StorageConfigInfo GetStorageConfigInfo()
    {
        var activeRoot = CurrentDataRoot;
        var (freeMb, totalMb) = GetDiskSpaceMb(activeRoot);
        var isWritable = TryEnsureDirectoryWritable(activeRoot, ".halo_write_probe");
        var appSettings = LoadAppSettings();

        return new StorageConfigInfo
        {
            DataRootPath = activeRoot,
            DefaultPath = DefaultDataRootPath,
            FallbackPath = FallbackDataRootPath,
            BootstrapFilePath = BootstrapFilePath,
            PreviousDataRootPath = PreviousDataRoot,
            LastModifiedUtc = LastModifiedUtc,
            IsUsingFallback = IsUsingFallback,
            IsWritable = isWritable,
            DiskFreeSpaceMb = freeMb,
            DiskTotalSpaceMb = totalMb,
            LogRetentionDays = appSettings.LogRetentionDays,
            LogChannels = GetLogChannelStatuses()
        };
    }

    public List<LogChannelStatus> GetLogChannelStatuses()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var logsDir = LogsDirectory;

        var definitions = new (string Channel, string Label, string FileName)[]
        {
            ("app", "系统运行日志 (App)", $"app-{today}.log"),
            ("llm", "大模型请求审计 (LLM Trace)", $"llm-trace-{today}.log"),
            ("tool", "工具执行日志 (Tool Exec)", $"tool-exec-{today}.log"),
        };

        var result = new List<LogChannelStatus>(definitions.Length);
        foreach (var def in definitions)
        {
            var fullPath = Path.Combine(logsDir, def.FileName);
            var info = new FileInfo(fullPath);
            result.Add(new LogChannelStatus
            {
                Channel = def.Channel,
                Label = def.Label,
                FileName = def.FileName,
                FilePath = fullPath,
                Exists = info.Exists,
                SizeBytes = info.Exists ? info.Length : 0L,
                LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") : null
            });
        }

        return result;
    }

    public static void EnsureStandardSubdirectories(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        foreach (var sub in ManagedSubDirectories)
        {
            Directory.CreateDirectory(Path.Combine(rootPath, sub));
        }
    }

    private static List<(string SourceFile, string DestFile)> CollectMigratableFiles(string sourceRoot, string targetRoot)
    {
        var result = new List<(string, string)>();
        var normalizedTargetWithSep = targetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var subDirName in MigratableSubDirectories)
        {
            var sourceSubDir = Path.Combine(sourceRoot, subDirName);
            if (!Directory.Exists(sourceSubDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(sourceSubDir, "*", SearchOption.AllDirectories))
            {
                var fullFile = Path.GetFullPath(file);
                // 防止当新目录位于旧目录内部时发生递归拷贝
                if (fullFile.StartsWith(normalizedTargetWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(sourceRoot, fullFile);
                var destFile = Path.Combine(targetRoot, relative);
                result.Add((fullFile, destFile));
            }
        }

        return result;
    }

    private static async Task CopyFileSharedAsync(string sourceFile, string destFile, CancellationToken ct)
    {
        await using var sourceStream = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true);

        await using var destStream = new FileStream(
            destFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        await sourceStream.CopyToAsync(destStream, 81920, ct).ConfigureAwait(false);
        await destStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static bool IsProtectedSystemDirectory(string fullPath)
    {
        var protectedRoots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"C:\Windows",
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\System Volume Information",
            @"C:\$Recycle.Bin"
        };

        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullPath, normRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (long FreeMb, long TotalMb) GetDiskSpaceMb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return (0, 0);
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return (0, 0);
            }

            return (drive.AvailableFreeSpace / (1024 * 1024), drive.TotalSize / (1024 * 1024));
        }
        catch
        {
            return (0, 0);
        }
    }


    public static bool TryEnsureDirectoryWritable(string directoryPath, string probeFileName = ".halo_write_probe") =>
        TryEnsureDirectoryWritable(directoryPath, probeFileName, out _);

     public static bool TryEnsureDirectoryWritable(string directoryPath, string probeFileName, out string? errorMessage)
     {
         errorMessage = null;
         try
         {
             Directory.CreateDirectory(directoryPath);
             var probePath = Path.Combine(directoryPath, $"{probeFileName}_{Guid.NewGuid():N}");
             // 注意：此处故意使用最普通的创建+写入+删除组合进行探测。
             // 严禁使用 FileShare.None / FileOptions.WriteThrough 等排他式直写标志——
             // 该组合极易被杀软行为监控误判为勒索软件特征，导致新构建的未签名程序被拦截写入、
             // 明明有权限却报 Access Denied（冒烟测试 2026-10-14 实证）。
             File.WriteAllText(probePath, "HALO_PROBE");
             File.Delete(probePath);
             return true;
         }
         catch (Exception ex)
         {
             errorMessage = ex.Message;
             return false;
         }
     }
}
