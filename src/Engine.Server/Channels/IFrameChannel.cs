using HaloAgent.Engine.Abstractions.Frames;

namespace HaloAgent.Engine.Server.Channels;

/// <summary>
/// 帧通道：把"发一帧 / 收一帧"抽象出来，传输实现可换。
/// 三种实现（P0 只做 Stdio，P1 补齐另两种）：
///   StdioChannel       stdin/stdout — 测试与独立联调，零依赖
///   NamedPipeChannel   生产形态    — 宿主 ↔ 引擎进程
///   InProcChannel      调试形态    — 引擎跑在宿主进程内（无序列化开销）
/// 契约：
///   - ReceiveAsync 返回 null 表示对端已关闭（EOF），不是错误。
///   - SendAsync 是线程安全的（内部加锁），允许多个调用方并发发送。
///   - DisposeAsync 必须幂等。
/// </summary>
public interface IFrameChannel : IAsyncDisposable
{
    /// <summary>通道的稳定标识，用于日志（如 "stdio" / "pipe:\\\\.\\pipe\\halo-xxx"）</summary>
    string Describe { get; }

    /// <summary>读取一个完整帧（已去 \n）。返回 null = EOF。</summary>
    ValueTask<Frame?> ReceiveAsync(CancellationToken ct);

    /// <summary>发送一个帧（自动追加 \n 并 flush）。</summary>
    ValueTask SendAsync(Frame frame, CancellationToken ct);
}
