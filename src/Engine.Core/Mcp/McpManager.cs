using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaloAgent.Engine.Core.Mcp;

/// <summary>
/// MCP server 管理器：从数据目录 mcp.json 读取配置，逐个启动连接。
/// 单个 server 连不上不影响其它（记录到 Errors）。
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = new();
    public List<(string server, string error)> Errors { get; } = new();

    public static async Task<McpManager> StartAsync(CancellationToken ct)
    {
        var mgr = new McpManager();
        var configPath = Path.Combine(
            Environment.GetEnvironmentVariable("HALO_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".haloagent"),
            "mcp.json");

        if (!File.Exists(configPath)) return mgr;

        McpConfigModel? cfg;
        try
        {
            var text = File.ReadAllText(configPath);
            cfg = JsonSerializer.Deserialize<McpConfigModel>(text);
        }
        catch (Exception ex)
        {
            mgr.Errors.Add(("config", "mcp.json 解析失败: " + ex.Message));
            return mgr;
        }
        if (cfg?.McpServers is null) return mgr;

        foreach (var (name, def) in cfg.McpServers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            try
            {
                var client = await McpClient.ConnectAsync(
                    name, def.Command, def.Args ?? new List<string>(), def.Env, ct)
                    .ConfigureAwait(false);
                mgr._clients.Add(client);
            }
            catch (Exception ex)
            {
                mgr.Errors.Add((name, ex.Message));
            }
        }
        return mgr;
    }

    public IReadOnlyList<McpClient> Clients => _clients;
    public bool IsEmpty => _clients.Count == 0;

    /// <summary>生成 MCP 工具定义（名字 mcp__&lt;server&gt;__&lt;tool&gt;）。</summary>
    public List<object> ToToolDefs()
    {
        var defs = new List<object>();
        foreach (var c in _clients)
        {
            foreach (var t in c.Tools)
            {
                defs.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name = "mcp__" + c.Name + "__" + t.Name,
                        description = t.Description,
                        parameters = t.InputSchema,
                    },
                });
            }
        }
        return defs;
    }

    public (McpClient client, string tool)? Resolve(string fullName)
    {
        const string prefix = "mcp__";
        if (!fullName.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rest = fullName[prefix.Length..];
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        if (sep <= 0) return null;

        var server = rest[..sep];
        var tool = rest[(sep + 2)..];

        var client = _clients.FirstOrDefault(c => c.Name == server);
        return client is null ? null : (client, tool);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) await c.DisposeAsync().ConfigureAwait(false);
        _clients.Clear();
    }

    private sealed class McpConfigModel
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, McpServerModel>? McpServers { get; set; }
    }

    private sealed class McpServerModel
    {
        [JsonPropertyName("command")] public string Command { get; set; } = "";
        [JsonPropertyName("args")] public List<string>? Args { get; set; }
        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
    }
}
