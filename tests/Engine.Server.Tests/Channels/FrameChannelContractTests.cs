using System.Text.Json;
using HaloAgent.Engine.Abstractions.Events;
using HaloAgent.Engine.Abstractions.Frames;
using HaloAgent.Engine.Abstractions.Serialization;
using HaloAgent.Engine.Core.Channels;
using Xunit;

namespace HaloAgent.Engine.Server.Tests.Channels;

/// <summary>
/// 通道必须满足的行为契约。
/// 三个实现（Pipe / Stdio / InProc）各继承一次，跑同一组用例。
///
/// 这是防止"本地测试通过、生产失败"的关键防线。
/// </summary>
public abstract class FrameChannelContractTests
{
    /// <summary>创建一对互连的通道：(a, b)。a.Send → b.Receive。</summary>
    protected abstract Task<(IFrameChannel a, IFrameChannel b)> CreatePairAsync();

    private static EventFrame SimpleEvent(int seq, string text) => new()
    {
        Seq = seq,
        Kind = "text.delta",
        Payload = JsonSerializer.SerializeToElement(
            new { text }, HaloJson.Options),
    };

    [Fact]
    public async Task Send_receive_single_frame()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        await using (b)
        {
            var sent = SimpleEvent(1, "hello");
            await a.SendAsync(sent, default);

            var r = await b.ReceiveAsync(default);
            Assert.False(r.IsClosed);
            Assert.False(r.IsMalformed);
            var got = Assert.IsType<EventFrame>(r.Frame);
            Assert.Equal(1, got.Seq);
            Assert.Equal("text.delta", got.Kind);
            Assert.Contains("hello", got.Payload!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task Multiple_frames_preserve_order()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        await using (b)
        {
            for (int i = 0; i < 100; i++)
                await a.SendAsync(SimpleEvent(i, $"m{i}"), default);

            for (int i = 0; i < 100; i++)
            {
                var r = await b.ReceiveAsync(default);
                var got = Assert.IsType<EventFrame>(r.Frame);
                Assert.Equal(i, got.Seq);
            }
        }
    }

    [Fact]
    public async Task Large_frame_1MB_roundtrip()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        await using (b)
        {
            // 1 MB 载荷（中文 3 字节/字，约 33 万汉字）
            var big = new string('中', 350_000);

            // 发送与接收并发：某些传输的内核缓冲放不下整帧，
            // 先发完再收会死锁（发送方等缓冲、测试等发送方）。
            var sendTask = Task.Run(() => a.SendAsync(SimpleEvent(1, big), default).AsTask());
            var r = await b.ReceiveAsync(Timeout(30));
            await sendTask;

            var got = Assert.IsType<EventFrame>(r.Frame);
            var text = JsonSerializer.Deserialize<TextDeltaPayload>(
                got.Payload!.Value, HaloJson.Options)!.Text;

            Assert.Equal(big.Length, text.Length);
        }
    }

    [Fact]
    public async Task Chinese_and_emoji_preserved()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        await using (b)
        {
            const string msg = "中文测试 😀🚀 é中";
            await a.SendAsync(SimpleEvent(1, msg), default);

            var r = await b.ReceiveAsync(default);
            var got = Assert.IsType<EventFrame>(r.Frame);
            // 内容级断言（不依赖编码器是否转义 astral 字符）：
            // .NET 6 的 UnsafeRelaxedJsonEscaping 仍会把 emoji 写成 \uD83D\uDE00，
            // GetRawText 断言会误杀。反序列化后比较才是语义。
            var text = JsonSerializer.Deserialize<TextDeltaPayload>(
                got.Payload!.Value, HaloJson.Options)!.Text;
            Assert.Equal(msg, text);
        }
    }

    [Fact]
    public async Task Concurrent_senders_do_not_interleave_frames()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        await using (b)
        {
            const int senders = 8;
            const int perSender = 50;

            var tasks = Enumerable.Range(0, senders).Select(s => Task.Run(async () =>
            {
                for (int i = 0; i < perSender; i++)
                    await a.SendAsync(SimpleEvent(s * 1000 + i, $"s{s}-{i}"), default);
            })).ToArray();

            var received = new List<int>();
            for (int i = 0; i < senders * perSender; i++)
            {
                var r = await b.ReceiveAsync(Timeout(20));
                Assert.False(r.IsMalformed, r.Error);
                received.Add((int)Assert.IsType<EventFrame>(r.Frame).Seq);
            }

            await Task.WhenAll(tasks);

            // 无重复无丢失
            Assert.Equal(senders * perSender, received.Distinct().Count());
        }
    }

    [Fact]
    public async Task Receive_returns_closed_when_peer_disposed()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        {
            await b.DisposeAsync();

            var r = await a.ReceiveAsync(Timeout(5));
            Assert.True(r.IsClosed);
        }
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var (a, b) = await CreatePairAsync();
        await a.DisposeAsync();
        await a.DisposeAsync();   // 第二次不应抛
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Receive_honors_cancellation()
    {
        var (a, b) = await CreatePairAsync();
        await using (a)
        await using (b)
        {
            using var cts = new CancellationTokenSource(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await b.ReceiveAsync(cts.Token));
        }
    }

    private static CancellationToken Timeout(int seconds)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        return cts.Token;
    }
}
