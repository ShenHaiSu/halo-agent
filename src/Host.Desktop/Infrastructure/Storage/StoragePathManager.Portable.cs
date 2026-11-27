using System.Text.Json;

namespace Host.Desktop.Infrastructure.Storage;

/// <summary>数据位置策略：由运行目录的私有数据标记与既有引导链共同决定。</summary>
public sealed record DataLocationPolicy(
    string BootstrapPath,
    string DataRootPath,
    string FallbackDataRootPath,
    bool IsPortable
);

/// <summary>私有数据标记的解析状态；损坏标记不得静默回退到全局数据根。</summary>
public enum PortableDataMarkerState
{
    Installed,
    Portable,
    InvalidMarker
}

public sealed partial class StoragePathManager
{
    public const string PortableDataMarkerFileName = "portable.data.json";
    public const string PortableDataDirectoryName = ".local-data";
    private const string PortableDataMarkerProperty = "useLocalData";

    /// <summary>
    /// 统一解析引导文件与数据根。显式参数优先，其次探测 exe 同级私有数据标记，
    /// 再考虑运维环境变量，最后走安装版全局默认链。此函数只做路径计算与标记读取，不写盘。
    /// </summary>
    public static DataLocationPolicy ResolveDataLocation(
        string? customBootstrap = null,
        string? customDefaultRoot = null,
        string? customFallbackRoot = null,
        string? exeDirectory = null
    )
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        var haloLocalAppDataDir = Path.Combine(localAppData, "HaloAgent");

        var hasCustom =
            !string.IsNullOrWhiteSpace(customBootstrap)
            || !string.IsNullOrWhiteSpace(customDefaultRoot)
            || !string.IsNullOrWhiteSpace(customFallbackRoot);

        if (!hasCustom)
        {
            var baseDir = !string.IsNullOrWhiteSpace(exeDirectory)
                ? exeDirectory
                : AppContext.BaseDirectory;
            var markerPath = Path.Combine(baseDir, PortableDataMarkerFileName);
            if (File.Exists(markerPath))
            {
                var state = InspectPortableDataMarker(markerPath);
                if (state == PortableDataMarkerState.InvalidMarker)
                {
                    throw new InvalidDataException(
                        $"私有数据标记损坏：{markerPath}。请检查文件内容是否包含 \"{PortableDataMarkerProperty}\": true。"
                    );
                }
                if (state == PortableDataMarkerState.Portable)
                {
                    var privateRoot = Path.Combine(baseDir, PortableDataDirectoryName);
                    return new DataLocationPolicy(
                        Path.Combine(privateRoot, "bootstrap.json"),
                        Path.Combine(privateRoot, "Data"),
                        Path.Combine(privateRoot, "Data"),
                        true
                    );
                }
            }
        }

        var envBootstrap = Environment.GetEnvironmentVariable("HALO_BOOTSTRAP_PATH");
        var bootstrapPath = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customBootstrap)
                ? customBootstrap
                : !string.IsNullOrWhiteSpace(envBootstrap)
                    ? envBootstrap
                    : Path.Combine(haloLocalAppDataDir, "bootstrap.json")
        );
        var defaultRoot = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customDefaultRoot)
                ? customDefaultRoot
                : DefaultCDriveDataRoot
        );
        var fallbackRoot = Path.GetFullPath(
            !string.IsNullOrWhiteSpace(customFallbackRoot)
                ? customFallbackRoot
                : Path.Combine(haloLocalAppDataDir, "Data")
        );
        return new DataLocationPolicy(bootstrapPath, defaultRoot, fallbackRoot, false);
    }

    /// <summary>供启动探针使用的保守解析：损坏标记返回私有路径且不抛，阻断启动由 Initialize 负责。</summary>
    public static DataLocationPolicy ResolveProbeDataLocation(string? exeDirectory = null)
    {
        try
        {
            return ResolveDataLocation(exeDirectory: exeDirectory);
        }
        catch (InvalidDataException)
        {
            var baseDir = !string.IsNullOrWhiteSpace(exeDirectory)
                ? exeDirectory
                : AppContext.BaseDirectory;
            var privateRoot = Path.Combine(baseDir, PortableDataDirectoryName);
            return new DataLocationPolicy(
                Path.Combine(privateRoot, "bootstrap.json"),
                Path.Combine(privateRoot, "Data"),
                Path.Combine(privateRoot, "Data"),
                true
            );
        }
    }

    public static PortableDataMarkerState InspectPortableDataMarker(string markerPath)
    {
        try
        {
            using var stream = File.OpenRead(markerPath);
            using var document = JsonDocument.Parse(stream);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PortableDataMarkerProperty, out var value)
                && value.ValueKind == JsonValueKind.True
                ? PortableDataMarkerState.Portable
                : PortableDataMarkerState.InvalidMarker;
        }
        catch (JsonException)
        {
            return PortableDataMarkerState.InvalidMarker;
        }
        catch (IOException)
        {
            return PortableDataMarkerState.InvalidMarker;
        }
        catch (UnauthorizedAccessException)
        {
            return PortableDataMarkerState.InvalidMarker;
        }
    }
}
