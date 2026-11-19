using System.Text;
using System.Text.Json;
using Host.Desktop.Core.Models;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 存储事件追加式持久化：写入数据根目录下 config/storage-events.jsonl（每行一条 JSON）。
/// 固定放 config/ 而非 logs/，避免日志目录随存储迁移被搬走时把审计文件也一起搬走。
/// 写入失败一律静默忽略，绝不影响存储切换主流程。
/// </summary>
public static class StorageEventStore
{
    private const string EventsFileName = "storage-events.jsonl";
    private static readonly object WriteLock = new();

    public static string GetEventsFilePath(StoragePathManager storageManager) =>
        Path.Combine(storageManager.ConfigDirectory, EventsFileName);

    /// <summary>追加一条事件记录；任何失败都吞掉，仅调试输出，不向上抛。</summary>
    public static void Append(
        StoragePathManager storageManager,
        string eventType,
        object? payload,
        long timestamp
    )
    {
        try
        {
            var path = GetEventsFilePath(storageManager);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var record = new StorageEventDto
            {
                Type = eventType,
                Payload = payload,
                Timestamp = timestamp
            };
            var line = JsonSerializer.Serialize(record, HaloJsonOptions.CompactCamelCase) + "\n";
            lock (WriteLock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // 审计写入失败不得影响存储切换；生产环境静默，调试期可见。
            System.Diagnostics.Debug.WriteLine($"[StorageEventStore] append failed: {ex.Message}");
        }
    }

    /// <summary>读取最近 limit 条事件（尾部 N 行、时间倒序）；文件不存在或读取失败返回空列表。</summary>
    public static List<StorageEventDto> ReadRecent(StoragePathManager storageManager, int limit)
    {
        if (limit <= 0)
        {
            return new List<StorageEventDto>();
        }

        try
        {
            var path = GetEventsFilePath(storageManager);
            if (!File.Exists(path))
            {
                return new List<StorageEventDto>();
            }

            var lines = File.ReadAllLines(path);
            var result = new List<StorageEventDto>(Math.Min(limit, lines.Length));
            for (var i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var record = JsonSerializer.Deserialize<StorageEventDto>(
                    line,
                    HaloJsonOptions.CompactCamelCase
                );
                if (record is not null)
                {
                    result.Add(record);
                }
            }

            return result;
        }
        catch
        {
            // 审计读取失败按“无历史”处理，不打断面板展示。
            return new List<StorageEventDto>();
        }
    }
}
