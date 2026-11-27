using System.Linq;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Storage;

public sealed partial class StoragePathManager
{
    /// <summary>
    /// 按受管子目录聚合存储占用。空目录也保留 0 值项；单文件读不到时逐项跳过，不影响整体统计。
    /// SizeBytes 始终是物理字节总和；sessions 行额外挂载主库页级明细（只读 PRAGMA 快照，不阻塞写入）。
    /// </summary>
    public StorageBreakdown GetStorageBreakdown()
    {
        var root = CurrentDataRoot;
        var items = new List<StorageBreakdownItem>();
        long total = 0;

        IReadOnlyList<WorkspaceDatabaseStorageDetail>? databaseDetails = null;
        try
        {
            databaseDetails = new WorkspaceDatabaseStorageReader(this).MeasureAll();
        }
        catch
        {
            databaseDetails = null;
        }

        foreach (var subDirectory in ManagedSubDirectories)
        {
            var (bytes, count) = SafeSumDirectory(Path.Combine(root, subDirectory));
            items.Add(
                new StorageBreakdownItem
                {
                    Name = subDirectory,
                    SizeBytes = bytes,
                    FileCount = count,
                    DatabaseDetails = string.Equals(
                        subDirectory,
                        "sessions",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? databaseDetails?.ToList()
                        : null
                }
            );
            total += bytes;
        }

        var (otherBytes, otherCount) = SafeSumRootFiles(root);
        items.Add(
            new StorageBreakdownItem
            {
                Name = "other",
                SizeBytes = otherBytes,
                FileCount = otherCount
            }
        );
        total += otherBytes;

        return new StorageBreakdown
        {
            RootPath = root,
            TotalBytes = total,
            Items = items
        };
    }

    /// <summary>迁移预估所需空间（MB）= 当前数据根目录下受管内容与根目录文件的总大小。</summary>
    internal long EstimateMigrationRequiredSpaceMb() =>
        GetStorageBreakdown().TotalBytes / (1024L * 1024L);

    /// <summary>目标目录是否已有内容（含隐藏项）；读取失败按“空”处理，不阻塞校验。</summary>
    internal static bool IsDirectoryNonEmpty(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static (long Bytes, int Count) SafeSumDirectory(string directory)
    {
        long bytes = 0;
        var count = 0;
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        try
        {
            foreach (
                var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            )
            {
                try
                {
                    var info = new FileInfo(file);
                    bytes += info.Length;
                    count++;
                }
                catch (UnauthorizedAccessException)
                {
                    // 单个文件无权读取：跳过，继续统计其余文件。
                }
                catch (IOException)
                {
                    // 单个文件被占用：跳过，继续统计其余文件。
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 整个目录遍历被拒：该目录按 0 计，不向上抛。
        }
        catch (IOException)
        {
            // 目录枚举 IO 异常：该目录按 0 计，不向上抛。
        }

        return (bytes, count);
    }

    private static (long Bytes, int Count) SafeSumRootFiles(string root)
    {
        long bytes = 0;
        var count = 0;
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    bytes += info.Length;
                    count++;
                }
                catch (UnauthorizedAccessException)
                {
                    // 根目录单个文件无权读取：跳过。
                }
                catch (IOException)
                {
                    // 根目录单个文件被占用：跳过。
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 根目录枚举被拒：按 0 计。
        }
        catch (IOException)
        {
            // 根目录枚举 IO 异常：按 0 计。
        }

        return (bytes, count);
    }
}
