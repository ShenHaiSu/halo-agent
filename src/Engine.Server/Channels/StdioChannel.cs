using System.Text;
using System.Text.Json;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;

namespace HaloAgent.Engine.Server.Channels;

/// <summary>
/// 走 stdin/stdout 的帧通道。
/// 用途：让引擎在**没有 UI、没有管道、没有端口**的情况下被测试与调试。
///   echo '{"t":"req","id":1,"m":"system.ping"}' | HaloAgent.Engine.exe --stdio
/// 注意：Windows 控制台默认编码是 GBK（936），这里强制 UTF-8，
///       否则中文载荷会变成乱码。宿主启动子进程时必须同样指定 UTF-8。
/// </summary>
public sealed class StdioChannel : IFrameChannel
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    public StdioChannel(Stream? input = null, Stream? output = null)
    {
        _input = input ?? Console.OpenStandardInput();
        _output = output ?? Console.OpenStandardOutput();
    }

    public string Describe => "stdio";

    public async ValueTask<Frame?> ReceiveAsync(CancellationToken ct)
    {
        // 逐字节读到 \n：不能直接用 StreamReader.ReadLineAsync，
        // 因为它在部分管道场景会预读并吞掉下一帧的开头。
        while (true)
        {
            var buf = new List<byte>(256);
            var one = new byte[1];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = await _input.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    // 真正的 EOF：调用方据此优雅退出。注意：有残缺半行也只能丢弃，
                    // 因为对端已关闭，再等也等不到 \n。
                    if (buf.Count == 0) return null;
                    break;
                }
                if (one[0] == (byte)'\n') break;
                if (one[0] == (byte)'\r') continue; // 容忍 CRLF
                buf.Add(one[0]);
            }

            var line = Encoding.UTF8.GetString(buf.ToArray());
            var dto = HaloJson.ReadFrame(line);
            var frame = dto switch
            {
                FrameDto.Request r => (Frame?)new RequestFrame { Id = r.Id, Method = r.Method, Params = r.Params },
                FrameDto.Event e => (Frame?)new EventFrame { Seq = e.Seq, Kind = e.Kind, Payload = e.Payload },
                FrameDto.Response res => (Frame?)new ResponseFrame
                {
                    Id = res.Id,
                    Ok = res.Ok,
                    Result = res.Result,
                },
                _ => null,
            };

            if (frame is not null) return frame;

            // 坏行 ≠ EOF：跳过并继续读下一行。
            // （历史教训：曾经把坏行直接当 EOF 返回，发送方若写了 BOM 前导，
            // 引擎会静默退出且退出码为 0，极难排查。现在坏行只记 stderr，不中断。）
            try { Console.Error.WriteLine($"[stdio] 忽略坏行: {line}"); } catch { }
        }
    }

    public async ValueTask SendAsync(Frame frame, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize<object>(frame, HaloJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // 不关 Console 的标准流：宿主可能在同进程内复用
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
