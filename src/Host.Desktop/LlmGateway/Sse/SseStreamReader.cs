using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Host.Desktop.LlmGateway.Sse;

/// <summary>
/// 标准 SSE 帧结构（事件类型 + 数据字符串）
/// </summary>
public readonly record struct SseFrame(string? EventType, string Data);

/// <summary>
/// 高性能零拷贝/低 GC UTF-8 SSE 流解析器。
/// 兼容 Win7 及所有现代服务器换行符（\r\n 与 \n），自动忽略心跳注释（: keep-alive）。
/// </summary>
public static class SseStreamReader
{
    public static async IAsyncEnumerable<SseFrame> ReadFramesAsync(
        Stream responseStream,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        if (responseStream == null)
        {
            yield break;
        }

        using var reader = new StreamReader(
            responseStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true
        );

        string? currentEvent = null;
        var dataBuilder = new StringBuilder();

        while (true)
        {
            // need22-P1 T2：取消全链透传（循环入口即检查并优雅退出；ReadLineAsync 经 WaitAsync(ct) 可中断等待）。
            if (ct.IsCancellationRequested)
                yield break;
            string? line;
            try
            {
                line = await reader.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (IOException)
            {
                // 网络连接被对端或本地取消强制关闭时安全退出
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            if (line is null) // EOF
            {
                if (dataBuilder.Length > 0)
                {
                    yield return new SseFrame(currentEvent, dataBuilder.ToString());
                }
                yield break;
            }

            if (line.Length == 0) // 空行标志一个 SSE Event Frame 结束
            {
                if (dataBuilder.Length > 0)
                {
                    yield return new SseFrame(currentEvent, dataBuilder.ToString());
                    dataBuilder.Clear();
                }
                currentEvent = null;
                continue;
            }

            if (line.StartsWith(':')) // SSE 注释心跳行（如 ": keep-alive"），直接忽略
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string payload = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.Append('\n');
                }
                dataBuilder.Append(payload);
            }
        }
    }
}
