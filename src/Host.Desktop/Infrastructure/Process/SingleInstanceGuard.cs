using System.Security.Cryptography;
using System.Text;

namespace Host.Desktop.Infrastructure.Process;

/// <summary>
/// need18：进程排他守卫。Mutex 跨进程 + 文件锁跨会话，持有到 Dispose。
/// 双层守卫：App 锁（会话级，早拦）+ Data 锁（机器级，兜底）。
/// 冻结项见 docs/need18/02，命名算法改动必须同步改 02 §3 + 06 命名单测。
/// 获取语义：new Mutex(false) 建柄不抢占，再 WaitOne(0) 原子判定。
/// 存活首实例持有 → false（被占）；上次崩溃遗留 → AbandonedMutexException（自动接管，视为成功）。
/// 同进程同线程递归（单测双获取）由 s_heldOwners 显式拦截，避免 Mutex 线程亲和递归误判。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public string MutexName { get; }
    public string ScopeLabel { get; } // "app" | "data"，供日志
    public bool IsFirstInstance { get; }
    public string? LockFilePath { get; } // 仅 data 锁有值（mutex-only 降级时也有值但无句柄）

    private Mutex? _mutex;
    private FileStream? _lockFile;
    private bool _disposed;

    /// <summary>诊断行接收器。Program 启动时接到私有 TraceStartup；单测可订阅断言关键字。</summary>
    public static Action<string>? TraceSink { get; set; }

    // 同进程已持有表：name -> 持有线程 ManagedThreadId。防同线程递归 WaitOne 误成功。
    private static readonly object s_sync = new();
    private static readonly Dictionary<string, int> s_heldOwners =
        new(StringComparer.OrdinalIgnoreCase);

    private SingleInstanceGuard(
        string mutexName,
        string scopeLabel,
        bool isFirstInstance,
        string? lockFilePath,
        Mutex? mutex,
        FileStream? lockFile
    )
    {
        MutexName = mutexName;
        ScopeLabel = scopeLabel;
        IsFirstInstance = isFirstInstance;
        LockFilePath = lockFilePath;
        _mutex = mutex;
        _lockFile = lockFile;
    }

    // —— 命名（纯函数，可单测，见 02 §3）——

    public static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var full = Path.GetFullPath(path.Trim());
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.ToLowerInvariant(); // Windows 不区分大小写；Linux 跑单测也不误判（仅测试用）
    }

    private static string Hash16(string normalizedKey)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes("halo-agent-v1|" + normalizedKey);
        return Convert.ToHexString(sha.ComputeHash(bytes))[..16].ToLowerInvariant();
    }

    private static string AppendTestSuffix(string baseName, string? testSaltSuffix)
    {
        if (string.IsNullOrEmpty(testSaltSuffix))
            return baseName;
        var sb = new StringBuilder();
        foreach (var ch in testSaltSuffix)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                sb.Append(ch);
            if (sb.Length >= 32)
                break;
        }
        var safe = sb.Length == 0 ? "T" : sb.ToString();
        return baseName + "-T-" + safe;
    }

    public static string BuildAppMutexName(string bootstrapFilePath) =>
        BuildAppMutexName(bootstrapFilePath, null);

    public static string BuildAppMutexName(string bootstrapFilePath, string? testSaltSuffix) =>
        AppendTestSuffix(
            @"Local\HaloAgent-App-" + Hash16(NormalizePathKey(bootstrapFilePath)),
            testSaltSuffix
        );

    public static string BuildDataMutexName(string dataRootPath) =>
        BuildDataMutexName(dataRootPath, null);

    public static string BuildDataMutexName(string dataRootPath, string? testSaltSuffix) =>
        AppendTestSuffix(
            @"Local\HaloAgent-Data-" + Hash16(NormalizePathKey(dataRootPath)),
            testSaltSuffix
        );

    /// <summary>
    /// need18-03 §2 探针：只拼接，不写盘（绝不调 StoragePathManager.Initialize）。
    /// 与 StoragePathManager.Initialize 的 bootstrap 解析保持一致：
    /// HALO_BOOTSTRAP_PATH &gt; LocalAppData\HaloAgent\bootstrap.json。
    /// </summary>
    public static string ResolveBootstrapProbePath()
    {
        var env = Environment.GetEnvironmentVariable("HALO_BOOTSTRAP_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);
        var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(lad, "HaloAgent", "bootstrap.json"));
    }

    /// <summary>旁路纯函数，供单测 T9（避免调真实 Main）。</summary>
    public static bool ShouldBypass() =>
        string.Equals(
            Environment.GetEnvironmentVariable("HALO_ALLOW_MULTIINSTANCE"),
            "1",
            StringComparison.Ordinal
        );

    // —— 获取（工厂，失败返回 null 或 IsFirstInstance=false，绝不抛到 Main 外）——

    public static SingleInstanceGuard? TryAcquireApp(
        string bootstrapFilePath,
        out string mutexName
    ) => TryAcquireApp(bootstrapFilePath, null, out mutexName);

    /// <summary>测试专用重载：saltSuffix 仅隔离单测命名空间，生产调用不传。</summary>
    public static SingleInstanceGuard? TryAcquireApp(
        string bootstrapFilePath,
        string? testSaltSuffix,
        out string mutexName
    )
    {
        string name;
        try
        {
            name = BuildAppMutexName(bootstrapFilePath, testSaltSuffix);
        }
        catch (Exception ex)
        {
            mutexName = string.Empty;
            Emit("stage=app-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
            return null;
        }
        mutexName = name;
        return AcquireMutexOnly(name, "app", null);
    }

    public static SingleInstanceGuard? TryAcquireData(
        string dataRootPath,
        out string mutexName,
        out string lockFilePath
    ) => TryAcquireData(dataRootPath, null, out mutexName, out lockFilePath);

    /// <summary>测试专用重载：saltSuffix 仅隔离单测命名空间，生产调用不传。</summary>
    public static SingleInstanceGuard? TryAcquireData(
        string dataRootPath,
        string? testSaltSuffix,
        out string mutexName,
        out string lockFilePath
    )
    {
        string name;
        string lockPath = string.Empty;
        try
        {
            name = BuildDataMutexName(dataRootPath, testSaltSuffix);
            lockPath = Path.Combine(Path.GetFullPath(dataRootPath), ".app.lock");
        }
        catch (Exception ex)
        {
            mutexName = string.Empty;
            lockFilePath = string.Empty;
            Emit("stage=data-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
            return null;
        }
        mutexName = name;
        lockFilePath = lockPath;

        // 先拿 Mutex（同进程递归先拦，跨进程靠 WaitOne(0)/Abandoned）。
        var guard = AcquireMutexOnly(name, "data", lockPath);
        if (guard == null)
            return null;
        if (!guard.IsFirstInstance)
            return guard; // 不是首实例：直接返回，不碰文件

        // 首实例：常驻文件锁（一次性 Create + Share.None，持有到退出，不轮询）。
        // 注意：guard._mutex 已持有 OS 句柄并已登记 s_heldOwners；文件失败时要释放它。
        FileStream? lockFile = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            lockFile = new FileStream(
                lockPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                true
            );
            var line =
                $"pid={Environment.ProcessId};startUtc={DateTime.UtcNow:O};mutex={name};hostname={Environment.MachineName};{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetBytes(line);
            lockFile.Write(bytes, 0, bytes.Length);
            lockFile.Flush(true);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 杀软/ACL 拦文件：降级为仅 Mutex 模式（05 §4），仍视为成功。
            Emit(
                "datalock-file-fallback-mutex-only scope=data name="
                    + name
                    + " lock="
                    + lockPath
                    + " err="
                    + ex.Message
            );
            guard.AttachLockFile(null);
            return guard;
        }
        catch (IOException ex)
        {
            // 跨会话/跨进程文件争用：第二个直接 IOException，视为被占。
            guard.ReleaseOwned(name);
            Emit(
                "stage=single-instance-blocked scope=data name="
                    + name
                    + " lock="
                    + lockPath
                    + " err="
                    + ex.Message
            );
            return new SingleInstanceGuard(name, "data", false, lockPath, null, null);
        }
        catch (Exception ex)
        {
            guard.ReleaseOwned(name);
            Emit("stage=data-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
            return null;
        }

        guard.AttachLockFile(lockFile);
        return guard;
    }

    private static SingleInstanceGuard? AcquireMutexOnly(
        string name,
        string scope,
        string? lockFilePath
    )
    {
        // 同进程同线程递归：本进程已持有且持有线程就是当前线程 → 直接判被占，不碰 OS。
        int currentTid = Environment.CurrentManagedThreadId;
        lock (s_sync)
        {
            if (s_heldOwners.TryGetValue(name, out var ownerTid) && ownerTid == currentTid)
            {
                return new SingleInstanceGuard(name, scope, false, lockFilePath, null, null);
            }
        }

        Mutex? mutex = null;
        bool abandoned = false;
        try
        {
            mutex = new Mutex(false, name, out _);
        }
        catch (UnauthorizedAccessException ex)
        {
            Emit("mutex-fallback-local name=" + name + " err=" + ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            Emit("stage=" + scope + "-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
            return null;
        }

        try
        {
            try
            {
                if (!mutex.WaitOne(0))
                {
                    // 存活首实例持有（跨进程或同进程他线程）→ 被占。
                    try
                    {
                        mutex.Dispose();
                    }
                    catch { }
                    return new SingleInstanceGuard(name, scope, false, lockFilePath, null, null);
                }
            }
            catch (AbandonedMutexException ex)
            {
                try
                {
                    if (ex.Mutex != null)
                        mutex = ex.Mutex;
                }
                catch { }
                abandoned = true;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            try
            {
                mutex.Dispose();
            }
            catch { }
            Emit("mutex-fallback-local name=" + name + " err=" + ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            try
            {
                mutex.Dispose();
            }
            catch { }
            Emit("stage=" + scope + "-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
            return null;
        }

        if (abandoned)
        {
            Emit("abandoned-acquired scope=" + scope + " name=" + name);
        }

        lock (s_sync)
        {
            s_heldOwners[name] = currentTid;
        }
        return new SingleInstanceGuard(name, scope, true, lockFilePath, mutex, null);
    }

    private void AttachLockFile(FileStream? fs)
    {
        _lockFile = fs;
    }

    private void ReleaseOwned(string name)
    {
        lock (s_sync)
        {
            s_heldOwners.Remove(name);
        }
        if (_mutex != null)
        {
            try
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
            }
            finally
            {
                try
                {
                    _mutex.Dispose();
                }
                catch { }
                _mutex = null;
            }
        }
        _disposed = true;
    }

    private static void Emit(string line)
    {
        try
        {
            TraceSink?.Invoke(line);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _lockFile?.Dispose();
        }
        catch { }
        _lockFile = null;
        if (_mutex != null)
        {
            lock (s_sync)
            {
                s_heldOwners.Remove(MutexName);
            }
            try
            {
                if (IsFirstInstance)
                {
                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch { }
                }
            }
            finally
            {
                try
                {
                    _mutex.Dispose();
                }
                catch { }
                _mutex = null;
            }
        }
    }
}
