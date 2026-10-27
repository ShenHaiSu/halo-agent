using System.Text;
using System.Text.Json;

namespace HaloAgent.Engine.Core.Mcp;

/// <summary>
/// MCP（Model Context Protocol）stdio 客户端。
/// 流程：spawn 子进程 → initialize（协议握手）→ notifications/initialized
/// → tools/list（拿工具清单）→ tools/call（执行工具）。
/// 关键约束：每行一条 JSON（JSON-RPC 2.0）；单请求超时 60s；
/// 单个 server 连不上不影响其它（记录到 errors）。
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private const int RequestTimeoutSeconds = 60;

    private readonly System.Diagnostics.Process _proc;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _pendingLock = new();
    private long _nextId;
    private readonly List<McpTool> _tools = new();

    public string Name { get; }
    public IReadOnlyList<McpTool> Tools => _tools;

    public sealed record McpTool(string Name, string Description, JsonElement InputSchema);

    private McpClient(string name, System.Diagnostics.Process proc)
    {
        Name = name;
        _proc = proc;
    }

    public static async Task<McpClient> ConnectAsync(
        string name, string command, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("无法启动 MCP server: " + command);
        var client = new McpClient(name, proc);

        _ = Task.Run(() => client.ReadLoopAsync(proc));
        _ = Task.Run(() => DrainStderrAsync(proc));

        await client.HandshakeAsync(ct).ConfigureAwait(false);
        await client.LoadToolsAsync(ct).ConfigureAwait(false);

        return client;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        await RequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "HaloAgent", version = "0.1.0" },
        }, ct).ConfigureAwait(false);

        await NotifyAsync("notifications/initialized", new { }, ct).ConfigureAwait(false);
    }

    private async Task LoadToolsAsync(CancellationToken ct)
    {
        var result = await RequestAsync("tools/list", new { }, ct).ConfigureAwait(false);

        if (!result.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array) return;

        foreach (var t in tools.EnumerateArray())
        {
            if (!t.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            var toolName = n.GetString();
            if (string.IsNullOrEmpty(toolName)) continue;

            var desc = t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";

            var schema = t.TryGetProperty("inputSchema", out var s)
                ? s.Clone()
                : JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();

            _tools.Add(new McpTool(toolName, desc, schema));
        }
    }

    public async Task<JsonElement> CallToolAsync(
        string toolName, JsonElement args, CancellationToken ct)
    {
        return await RequestAsync("tools/call", new
        {
            name = toolName,
            arguments = args,
        }, ct).ConfigureAwait(false);
    }

    // ---------------- 内部 ----------------

    private async Task<JsonElement> RequestAsync(string method, object prms, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingLock) _pending[id] = tcs;

        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = prms,
        });

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _proc.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("MCP server「" + Name + "」请求 " + method + " 超时（" + RequestTimeoutSeconds + "s）");
        }
        finally
        {
            lock (_pendingLock) _pending.Remove(id);
        }
    }

    private async Task NotifyAsync(string method, object prms, CancellationToken ct)
    {
        var notify = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = prms,
        });

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _proc.StandardInput.WriteLineAsync(notify).ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(System.Diagnostics.Process proc)
    {
        try
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("id", out var idEl) ||
                        idEl.ValueKind != JsonValueKind.Number) continue;

                    var id = idEl.GetInt64();
                    TaskCompletionSource<JsonElement>? tcs;
                    lock (_pendingLock) _pending.TryGetValue(id, out tcs);
                    if (tcs is null) continue;

                    if (root.TryGetProperty("error", out var err))
                    {
                        tcs.TrySetException(new InvalidOperationException(
                            "MCP 错误: " + err.GetRawText()));
                    }
                    else if (root.TryGetProperty("result", out var res))
                    {
                        tcs.TrySetResult(res.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(default);
                    }
                }
                catch (JsonException) { /* 坏行跳过 */ }
            }
        }
        catch { }
    }

    private static async Task DrainStderrAsync(System.Diagnostics.Process proc)
    {
        try
        {
            while (!proc.StandardError.EndOfStream)
                await proc.StandardError.ReadLineAsync().ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _proc.StandardInput.Close();
            if (!_proc.WaitForExit(2000)) _proc.Kill(entireProcessTree: true);
        }
        catch { }
        _proc.Dispose();
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}
