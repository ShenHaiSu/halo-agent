using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Logging;

namespace Host.Desktop.Infrastructure.Storage;

public sealed partial class StoragePathManager
{
    /// <summary>
    /// 修改存储根目录并按需迁移历史数据（config / workspaces / sessions / logs），原子更新 bootstrap.json。
    /// </summary>
    public async Task<StorageMigrationResult> ChangeDataRootAsync(
        string newTargetPath,
        bool migrateExistingData,
        Action<int, string>? onProgress = null,
        CancellationToken ct = default
    )
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
                onProgress?.Invoke(
                    percent,
                    $"正在迁移文件 ({i + 1}/{totalFiles}): {Path.GetFileName(sourceFile)}"
                );
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

        await AtomicFileWriter
            .WriteJsonAtomicAsync(BootstrapFilePath, bootstrap, ct)
            .ConfigureAwait(false);

        lock (_stateLock)
        {
            _previousDataRoot = sourceRoot;
            _currentDataRoot = targetRoot;
            _lastModifiedUtc = nowUtc;
            _isUsingFallback = string.Equals(
                targetRoot,
                FallbackDataRootPath,
                StringComparison.OrdinalIgnoreCase
            );
        }

        EnsureDefaultAppSettings();
        HaloLogger.SwitchLogsDirectory(LogsDirectory);
        HaloLogger.App.Info(
            $"Storage dataRootPath switched from '{sourceRoot}' to '{targetRoot}' (migrateExistingData={migrateExistingData}, files={migratedCount}, bytes={migratedBytes})."
        );
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
            result.Add(
                new LogChannelStatus
                {
                    Channel = def.Channel,
                    Label = def.Label,
                    FileName = def.FileName,
                    FilePath = fullPath,
                    Exists = info.Exists,
                    SizeBytes = info.Exists ? info.Length : 0L,
                    LastWriteTimeUtc = info.Exists
                        ? info.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : null
                }
            );
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

    private static List<(string SourceFile, string DestFile)> CollectMigratableFiles(
        string sourceRoot,
        string targetRoot
    )
    {
        var result = new List<(string, string)>();
        var normalizedTargetWithSep =
            targetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var subDirName in MigratableSubDirectories)
        {
            var sourceSubDir = Path.Combine(sourceRoot, subDirName);
            if (!Directory.Exists(sourceSubDir))
            {
                continue;
            }

            foreach (
                var file in Directory.EnumerateFiles(sourceSubDir, "*", SearchOption.AllDirectories)
            )
            {
                var fullFile = Path.GetFullPath(file);
                // 防止当新目录位于旧目录内部时发生递归拷贝
                if (
                    fullFile.StartsWith(normalizedTargetWithSep, StringComparison.OrdinalIgnoreCase)
                )
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

    private static async Task CopyFileSharedAsync(
        string sourceFile,
        string destFile,
        CancellationToken ct
    )
    {
        await using var sourceStream = new FileStream(
            sourceFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: true
        );

        await using var destStream = new FileStream(
            destFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true
        );

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
            if (
                string.Equals(fullPath, normRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(
                    normRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
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

    public static bool TryEnsureDirectoryWritable(
        string directoryPath,
        string probeFileName = ".halo_write_probe"
    ) => TryEnsureDirectoryWritable(directoryPath, probeFileName, out _);

    public static bool TryEnsureDirectoryWritable(
        string directoryPath,
        string probeFileName,
        out string? errorMessage
    )
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
