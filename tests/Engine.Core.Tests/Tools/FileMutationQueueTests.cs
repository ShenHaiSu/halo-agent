using HaloAgent.Engine.Core.Tools;
using Xunit;

namespace HaloAgent.Engine.Core.Tests.Tools;

public sealed class FileMutationQueueTests
{
    [Fact]
    public async Task Same_file_calls_are_serialized()
    {
        var dir = Path.Combine(Path.GetTempPath(), "halo-fmq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt");

        var enter1 = 0L;
        var exit1 = 0L;
        var enter2 = 0L;
        var exit2 = 0L;

        var tcs = new TaskCompletionSource<bool>();
        var t1 = FileMutationQueue.RunAsync(file, async () =>
        {
            enter1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await tcs.Task;
            exit1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        });
        // 确保 t1 已入队
        await Task.Delay(50);
        var t2 = FileMutationQueue.RunAsync(file, async () =>
        {
            enter2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Task.Yield();
            exit2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return true;
        });
        await Task.Delay(50);
        tcs.SetResult(true);
        await Task.WhenAll(t1, t2);

        // t2 进入必须在 t1 退出之后（串行）
        Assert.True(enter2 >= exit1, $"enter2={enter2} exit1={exit1}");
        Assert.True(enter1 <= exit1);
        Assert.True(enter2 <= exit2);
        Assert.Equal(0, FileMutationQueue.PendingKeyCount);
    }

    [Fact]
    public async Task Different_files_run_concurrently()
    {
        var dir = Path.Combine(Path.GetTempPath(), "halo-fmq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");

        var gate = new TaskCompletionSource<bool>();
        var bDone = false;
        var ta = FileMutationQueue.RunAsync(a, async () =>
        {
            await gate.Task;
            return true;
        });
        var tb = FileMutationQueue.RunAsync(b, async () =>
        {
            await Task.Yield();
            bDone = true;
            return true;
        });

        await Task.WhenAny(tb, Task.Delay(2000));
        Assert.True(bDone);
        gate.SetResult(true);
        await Task.WhenAll(ta, tb);
        Assert.Equal(0, FileMutationQueue.PendingKeyCount);
    }

    [Fact]
    public async Task Exception_does_not_block_queue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "halo-fmq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileMutationQueue.RunAsync<string>(file, () => throw new InvalidOperationException("boom")));

        var ok = await FileMutationQueue.RunAsync(file, () => Task.FromResult("fine"));
        Assert.Equal("fine", ok);
        Assert.Equal(0, FileMutationQueue.PendingKeyCount);
    }

    [Fact]
    public async Task Queue_drains_after_completion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "halo-fmq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt");
        await FileMutationQueue.RunAsync(file, () => Task.CompletedTask);
        Assert.Equal(0, FileMutationQueue.PendingKeyCount);
    }
}
