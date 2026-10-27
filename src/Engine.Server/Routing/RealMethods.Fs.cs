using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Storage;

namespace HaloAgent.Engine.Server.Routing;

public sealed partial class RealMethods
{
    // ---------- system.diagnostics（契约要求 {}，不是 {ok:true}） ----------
    public Task<object?> SystemDiagnosticsAsync(JsonElement? p, CancellationToken ct) =>
        Task.FromResult<object?>(new { });

    // ---------- tool.mcpStatus ----------
    public Task<object?> ToolMcpStatusAsync(JsonElement? p, CancellationToken ct)
    {
        var servers = new List<object>();
        var path = Path.Combine(_dataDir, "mcp.json");
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("mcpServers", out var ms)
                    && ms.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in ms.EnumerateObject())
                    {
                        servers.Add(new
                        {
                            name = prop.Name,
                            connected = false,
                            toolCount = 0,
                            error = (string?)null,
                        });
                    }
                }
            }
            catch
            {
                // 解析失败则返回空列表（settings.mcp.save 已保证写入合法 JSON）
            }
        }

        servers.Sort((a, b) => string.CompareOrdinal(
            ((dynamic)a).name as string, ((dynamic)b).name as string));
        return Task.FromResult<object?>(new { servers });
    }

    // ---------- update.check（语义化降级：无更新源，不断言失败） ----------
    public Task<object?> UpdateCheckAsync(JsonElement? p, CancellationToken ct) =>
        Task.FromResult<object?>(new
        {
            available = false,
            latestVersion = (string?)null,
            notes = (string?)"未配置更新源",
        });

    public Task<object?> UpdateInstallAsync(JsonElement? p, CancellationToken ct) =>
        Task.FromResult<object?>(new { started = false });

    // ---------- fs.listDir ----------
    public Task<object?> FsListDirAsync(JsonElement? p, CancellationToken ct)
    {
        var raw = p?.TryGetProperty("path", out var v) == true ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(raw))
            throw RpcException.ParamsInvalid("path 不能为空");

        var dir = raw;
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(_session.Cwd, dir);

        DirectoryInfo di;
        try { di = new DirectoryInfo(dir); }
        catch (Exception ex)
        {
            throw new RpcException(RpcError.FsNotFound, "路径无效：" + ex.Message);
        }
        if (!di.Exists)
            throw new RpcException(RpcError.FsNotFound, "目录不存在: " + raw);

        var items = new List<object>();
        try
        {
            foreach (var entry in di.EnumerateFileSystemInfos()
                         .OrderByDescending(e => e is DirectoryInfo)
                         .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(200))
            {
                ct.ThrowIfCancellationRequested();
                long size = 0;
                if (entry is FileInfo fi)
                {
                    try { size = fi.Length; } catch { }
                }
                items.Add(new { name = entry.Name, isDir = entry is DirectoryInfo, size });
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new RpcException(RpcError.FsNotFound, "无权限访问: " + raw);
        }

        return Task.FromResult<object?>(new { items });
    }

    // ---------- fs.completePaths ----------
    public Task<object?> FsCompletePathsAsync(JsonElement? p, CancellationToken ct)
    {
        var prefix = p?.TryGetProperty("prefix", out var pv) == true ? pv.GetString() ?? "" : "";

        string dirPart, namePart;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            dirPart = _session.Cwd;
            namePart = "";
        }
        else
        {
            var normalized = prefix.Replace('\\', '/');
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash < 0)
            {
                dirPart = _session.Cwd;
                namePart = normalized;
            }
            else
            {
                dirPart = normalized[..lastSlash];
                if (dirPart.Length == 0) dirPart = "/";
                namePart = normalized[(lastSlash + 1)..];
            }
        }

        if (!Path.IsPathRooted(dirPart))
            dirPart = Path.Combine(_session.Cwd, dirPart);

        var items = new List<object>();
        try
        {
            var di = new DirectoryInfo(dirPart);
            if (di.Exists)
            {
                foreach (var entry in di.EnumerateFileSystemInfos()
                             .Where(e => e.Name.StartsWith(namePart, StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(e => e is DirectoryInfo)
                             .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                             .Take(50))
                {
                    items.Add(new
                    {
                        path = entry.FullName.Replace('\\', '/'),
                        isDir = entry is DirectoryInfo,
                    });
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return Task.FromResult<object?>(new { items });
    }

    // ---------- fs.validatePath ----------
    public Task<object?> FsValidatePathAsync(JsonElement? p, CancellationToken ct)
    {
        var raw = p?.TryGetProperty("path", out var v) == true ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(raw))
            throw RpcException.ParamsInvalid("path 不能为空");

        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(raw)
                ? raw : Path.Combine(_session.Cwd, raw));
            var isDir = Directory.Exists(full);
            var exists = isDir || File.Exists(full);
            return Task.FromResult<object?>(new
            {
                valid = exists,
                normalized = full.Replace('\\', '/'),
                isDir,
                error = exists ? (string?)null : "路径不存在",
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object?>(new
            {
                valid = false,
                normalized = raw,
                isDir = false,
                error = (string?)ex.Message,
            });
        }
    }
}
