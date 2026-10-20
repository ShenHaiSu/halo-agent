using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Host.Desktop.Infrastructure.Process;
using Xunit;

namespace HaloAgent.Tests;

// need18-06 §2：单实例互斥验收（纯逻辑，不启动 Cef，不碰 C:\HaloAgentData，不弹 MessageBox）。
public sealed class Stage18SingleInstanceTests
{
    private static string NewSalt() => "TestRun-" + Guid.NewGuid().ToString("N");

    private static List<string> CaptureTrace(out Action restore)
    {
        var lines = new List<string>();
        var prev = SingleInstanceGuard.TraceSink;
        var prevAct = NativeWindowActivator.TraceSink;
        SingleInstanceGuard.TraceSink = s => { lock (lines) lines.Add(s); };
        NativeWindowActivator.TraceSink = s => { lock (lines) lines.Add(s); };
        restore = () =>
        {
            SingleInstanceGuard.TraceSink = prev;
            NativeWindowActivator.TraceSink = prevAct;
        };
        return lines;
    }

    [Fact]
    public void Normalize_SamePath_DifferentCase_Equal()
    {
        var a = SingleInstanceGuard.NormalizePathKey("C:\\HaloAgentData");
        var b = SingleInstanceGuard.NormalizePathKey("c:\\haloagentdata\\");
        Assert.Equal(a, b);
        Assert.NotEqual(string.Empty, a);
    }

    [Fact]
    public void MutexName_Stable_And_Prefixed()
    {
        var probe = Path.Combine(Path.GetTempPath(), "halo-need18-probe.json");
        var n1 = SingleInstanceGuard.BuildAppMutexName(probe);
        var n2 = SingleInstanceGuard.BuildAppMutexName(probe);
        Assert.Equal(n1, n2);
        Assert.StartsWith(@"Local\HaloAgent-App-", n1);
        var tail = n1.Substring(@"Local\HaloAgent-App-".Length);
        Assert.Equal(16, tail.Length);
        Assert.Matches("^[0-9a-f]{16}$", tail);
    }

    [Fact]
    public void AppAndData_Namespace_Isolated()
    {
        var p = Path.Combine(Path.GetTempPath(), "halo-need18-ns.json");
        var app = SingleInstanceGuard.BuildAppMutexName(p);
        var data = SingleInstanceGuard.BuildDataMutexName(p);
        Assert.NotEqual(app, data);
    }

    [Fact]
    public void SecondAcquire_SameProcess_IsNotFirst()
    {
        var probe = Path.Combine(Path.GetTempPath(), "halo-need18-" + Guid.NewGuid().ToString("N") + ".json");
        var salt = NewSalt();
        var g1 = SingleInstanceGuard.TryAcquireApp(probe, salt, out var n1);
        Assert.NotNull(g1);
        Assert.True(g1!.IsFirstInstance);
        try
        {
            var g2 = SingleInstanceGuard.TryAcquireApp(probe, salt, out var n2);
            Assert.NotNull(g2);
            Assert.Equal(n1, n2);
            Assert.False(g2!.IsFirstInstance);
            g2.Dispose();
        }
        finally
        {
            g1.Dispose();
        }
    }

    [Fact]
    public void AbandonedMutex_CanReacquire()
    {
        var probe = Path.Combine(Path.GetTempPath(), "halo-need18-ab-" + Guid.NewGuid().ToString("N") + ".json");
        var salt = NewSalt();
        // worker 独占创建并持有后直接退出（不 Release、不 Dispose，常驻泄漏），模拟崩溃遗留。
        // 主线程此前无任何该命名 Mutex 句柄 → 下一个 new Mutex(true) 必得 Abandoned。
        var doomedName = SingleInstanceGuard.BuildAppMutexName(probe, salt);
        var leaked = new List<Mutex>();
        var owned = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            var m = new Mutex(true, doomedName, out _);
            lock (leaked) leaked.Add(m); // 常驻泄漏，防终结器提前关柄
            owned.Set();
            // 线程结束即 abandon，不 Release。
        });
        t.IsBackground = true;
        t.Start();
        Assert.True(owned.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(t.Join(TimeSpan.FromSeconds(5)));

        var traces = CaptureTrace(out var restore);
        try
        {
            var g = SingleInstanceGuard.TryAcquireApp(probe, salt, out _);
            Assert.NotNull(g);
            Assert.True(g!.IsFirstInstance);
            Assert.Contains(traces, s => s.Contains("abandoned-acquired"));
            g.Dispose();
        }
        finally
        {
            restore();
            lock (leaked)
            {
                foreach (var m in leaked)
                {
                    try { m.ReleaseMutex(); } catch { }
                    try { m.Dispose(); } catch { }
                }
                leaked.Clear();
            }
        }
    }

    [Fact]
    public void DataLock_SecondFails_ThenSucceedsAfterDispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "halo-need18-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var salt = NewSalt();
        try
        {
            var a = SingleInstanceGuard.TryAcquireData(root, salt, out _, out _);
            Assert.NotNull(a);
            Assert.True(a!.IsFirstInstance);

            var b = SingleInstanceGuard.TryAcquireData(root, salt, out _, out _);
            Assert.NotNull(b);
            Assert.False(b!.IsFirstInstance);
            b.Dispose();

            a.Dispose();
            var c = SingleInstanceGuard.TryAcquireData(root, salt, out _, out _);
            Assert.NotNull(c);
            Assert.True(c!.IsFirstInstance);
            c.Dispose();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void DataLock_FileContent_HasPid()
    {
        var root = Path.Combine(Path.GetTempPath(), "halo-need18-pid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var salt = NewSalt();
        try
        {
            var g = SingleInstanceGuard.TryAcquireData(root, salt, out _, out var lockPath);
            Assert.NotNull(g);
            Assert.True(g!.IsFirstInstance);
            Assert.NotNull(lockPath);
            Assert.True(File.Exists(lockPath));
            // .app.lock 常驻 Share.None 独占持有中：持有期间任何第二句柄都打不开（含本进程），
            // 故先 Dispose 释放句柄再读盘（文件本身仍保留诊断行）。
            g.Dispose();
            var text = File.ReadAllText(lockPath!);
            Assert.Contains("pid=", text);
            Assert.Contains($"pid={Environment.ProcessId}", text);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ProbePath_EnvOverride_Wins()
    {
        var prev = Environment.GetEnvironmentVariable("HALO_BOOTSTRAP_PATH");
        try
        {
            var custom = Path.Combine(Path.GetTempPath(), "halo-need18-" + Guid.NewGuid().ToString("N") + ".json");
            Environment.SetEnvironmentVariable("HALO_BOOTSTRAP_PATH", custom);
            Assert.Equal(Path.GetFullPath(custom), SingleInstanceGuard.ResolveBootstrapProbePath());

            Environment.SetEnvironmentVariable("HALO_BOOTSTRAP_PATH", null);
            var fallback = SingleInstanceGuard.ResolveBootstrapProbePath();
            Assert.EndsWith(Path.Combine("HaloAgent", "bootstrap.json"), fallback, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALO_BOOTSTRAP_PATH", prev);
        }
    }

    [Fact]
    public void BypassEnv_Skips_All()
    {
        var prev = Environment.GetEnvironmentVariable("HALO_ALLOW_MULTIINSTANCE");
        try
        {
            Environment.SetEnvironmentVariable("HALO_ALLOW_MULTIINSTANCE", "1");
            Assert.True(SingleInstanceGuard.ShouldBypass());
            Environment.SetEnvironmentVariable("HALO_ALLOW_MULTIINSTANCE", null);
            Assert.False(SingleInstanceGuard.ShouldBypass());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALO_ALLOW_MULTIINSTANCE", prev);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var probe = Path.Combine(Path.GetTempPath(), "halo-need18-" + Guid.NewGuid().ToString("N") + ".json");
        var g = SingleInstanceGuard.TryAcquireApp(probe, NewSalt(), out _);
        Assert.NotNull(g);
        g!.Dispose();
        g.Dispose(); // 不抛
    }

    [Fact]
    public void FileLockDenied_FallsBack_MutexOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "halo-need18-deny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var salt = NewSalt();
        // 预占 .app.lock 为目录，使 FileStream Create 抛 UnauthorizedAccessException，触发 mutex-only 降级。
        var lockAsDir = Path.Combine(root, ".app.lock");
        Directory.CreateDirectory(lockAsDir);
        var traces = CaptureTrace(out var restore);
        try
        {
            var g = SingleInstanceGuard.TryAcquireData(root, salt, out _, out _);
            Assert.NotNull(g);
            Assert.True(g!.IsFirstInstance);
            Assert.Contains(traces, s => s.Contains("datalock-file-fallback-mutex-only"));
            g.Dispose();
        }
        finally
        {
            restore();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
