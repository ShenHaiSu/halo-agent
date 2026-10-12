using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>
/// 全局统一 JSON 序列化配置（小驼峰命名）与防断电损坏原子写入器。
/// </summary>
public static class HaloJsonOptions
{
    public static readonly JsonSerializerOptions IndentedCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions CompactCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>
/// 配置文件防损坏原子写入工具：写临时文件 -> Flush 磁盘 -> 原子替换。
/// </summary>
public static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteJsonAtomicAsync<T>(string filePath, T data, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(data, HaloJsonOptions.IndentedCamelCase);
            await using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            await using (var sw = new StreamWriter(fs, Utf8NoBom))
            {
                await sw.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
                await sw.FlushAsync().ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 忽略残留临时文件清理异常 */ }
            }
        }
    }

    public static void WriteJsonAtomic<T>(string filePath, T data)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(data, HaloJsonOptions.IndentedCamelCase);
            using (var fs = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            using (var sw = new StreamWriter(fs, Utf8NoBom))
            {
                sw.Write(json);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 忽略残留临时文件清理异常 */ }
            }
        }
    }

    public static T? ReadJsonOrDefault<T>(string filePath, T? defaultValue = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return defaultValue;
            }

            var json = File.ReadAllText(filePath, Utf8NoBom);
            if (string.IsNullOrWhiteSpace(json))
            {
                return defaultValue;
            }

            return JsonSerializer.Deserialize<T>(json, HaloJsonOptions.IndentedCamelCase) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public static async Task<T?> ReadJsonOrDefaultAsync<T>(string filePath, T? defaultValue = default, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return defaultValue;
            }

            var json = await File.ReadAllTextAsync(filePath, Utf8NoBom, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return defaultValue;
            }

            return JsonSerializer.Deserialize<T>(json, HaloJsonOptions.IndentedCamelCase) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
}
