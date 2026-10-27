using System.Text.Json;
using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>
/// 单实例守卫：互斥量快速分流 + 短命管道转发激活请求。
/// 次实例 Signal 失败必须重试（首实例可能还没开始监听），否则“连点没反应”。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\HaloAgent.SingleInstance.v1";
    private static string SignalPipe =>
        "haloagent-activate-" + CurrentUserHash();

    private Mutex? _mutex;
    private CancellationTokenSource? _listenCts;

    public bool IsFirstInstance { get; private set; }

    public event Action<string[]>? ActivateRequested;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
        return createdNew;
    }

    /// <summary>强制取得所有权（首实例无响应时的极端兜底；真正一致性由引擎侧保证）。</summary>
    public bool ForceTakeOwnership()
    {
        IsFirstInstance = true;
        return true;
    }

    public void StartListening()
    {
        if (!IsFirstInstance) return;
        _listenCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_listenCts.Token));
    }

    public static async Task<bool> SignalFirstInstanceAsync(string[] args)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", SignalPipe, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(2000).ConfigureAwait(false);
            await using var w = new StreamWriter(client, System.Text.Encoding.UTF8) { AutoFlush = true };
            await w.WriteLineAsync(JsonSerializer.Serialize(args)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> SignalWithRetryAsync(string[] args, int attempts = 10)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (await SignalFirstInstanceAsync(args).ConfigureAwait(false))
                return true;
            await Task.Delay(300).ConfigureAwait(false);
        }
        return false;
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new System.IO.Pipes.NamedPipeServerStream(
                    SignalPipe, System.IO.Pipes.PipeDirection.In, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var r = new StreamReader(server, System.Text.Encoding.UTF8);
                var line = await r.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var args = JsonSerializer.Deserialize<string[]>(line) ?? Array.Empty<string>();
                    ActivateRequested?.Invoke(args);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 单次失败后继续监听 */ }
        }
    }

    private static string CurrentUserHash()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "anon";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sid));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public void Dispose()
    {
        _listenCts?.Cancel();
        _listenCts?.Dispose();
        if (IsFirstInstance && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* 未持有时释放会抛，忽略 */ }
        }
        _mutex?.Dispose();
    }
}
