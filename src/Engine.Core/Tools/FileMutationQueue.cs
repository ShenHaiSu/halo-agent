using System.Collections.Concurrent;

namespace HaloAgent.Engine.Core.Tools;

/// <summary>
/// 按文件串行化的写操作队列。
///
/// ============ 解决什么问题 ============
/// 一条 assistant 消息可以同时发多个工具调用。并行执行后，
/// 同一条消息里的 write_file(a) 与 edit(a) 会同时读 a、各自写回，
/// 结果是后写的覆盖先写的 —— 数据静默丢失。
///
/// ============ 语义 ============
///   - 同一 key：严格串行（FIFO，按调用顺序）
///   - 不同 key：完全并发
///   - fn 抛异常：异常向调用方传播，但队列必须继续可用（不能被一个失败卡死）
///   - 链是「尾接尾」：新任务挂在上一个任务的完成之后，不建全局锁
///
/// ============ 为什么不用 SemaphoreSlim 字典 ============
/// SemaphoreSlim 需要引用计数回收（否则字典无限增长，或回收时误删正在等待的锁）。
/// Task 链式续接天然无此问题：链尾一空就从字典删掉。
/// </summary>
public static class FileMutationQueue
{
    private static readonly ConcurrentDictionary<string, Task> Tails =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// 串行执行 fn。filePath 必须是已解析的绝对路径；
    /// 内部会再做一次 realpath 归一作为 key。
    /// </summary>
    public static async Task<T> RunAsync<T>(string filePath, Func<Task<T>> fn)
    {
        var key = ToolPaths.ResolveRealistic(filePath);

        Task previous;
        TaskCompletionSource<bool> gate;
        lock (Tails)
        {
            // 原子地「取出旧尾 + 安装新尾」：锁内只做字典操作，不 await
            previous = Tails.TryGetValue(key, out var tail)
                ? tail
                : Task.CompletedTask;
            gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Tails[key] = gate.Task;
        }

        try
        {
            // 前一个任务无论成功失败都要放行（失败已由它自己的调用方处理）
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // 吞掉前驱的异常：那是别人的结果，不该串到我这里
        }

        try
        {
            return await fn().ConfigureAwait(false);
        }
        finally
        {
            // 收尾：如果当前尾还是自己，删掉；否则留给后来者
            lock (Tails)
            {
                if (Tails.TryGetValue(key, out var tail) && ReferenceEquals(tail, gate.Task))
                    Tails.TryRemove(key, out _);
            }
            gate.TrySetResult(true);
        }
    }

    /// <summary>无返回值版本。</summary>
    public static Task RunAsync(string filePath, Func<Task> fn) =>
        RunAsync<bool>(filePath, async () => { await fn().ConfigureAwait(false); return true; });

    /// <summary>仅测试用：断言队列已清空（无残留 key）。</summary>
    internal static int PendingKeyCount => Tails.Count;
}
