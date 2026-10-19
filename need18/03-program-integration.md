# need18-03 Program 接入：双获取点与退出语义

> 对应总览：[00-README](./00-README.md)，依赖 [02](./02-mutex-and-lock-design.md) 的命名与类规约。改动文件：`develop/src/Host.Desktop/Program.cs`（唯一存量修改，本篇）。

## 1. 接入位置（diff 级描述）

现 `Program.cs:29-148 Main()` 按顺序是 `main-enter → TLS → DPI → 异常兜底 → Storage.Initialize(L59) → Logger → CefSettings → Cef.Initialize(L126) → Application.Run(L139)`。

插入两个获取点，一个静态持有：

```csharp
internal static class Program
{
    private static SingleInstanceGuard? _appGuard;   // 新增：static 防 GC
    private static SingleInstanceGuard? _dataGuard;  // 新增：static 防 GC
    private const int SecondInstanceExitCode = 42;   // 新增：约定的“重复实例”退出码（06 断言用）

    [STAThread]
    static void Main()
    {
        TraceStartup("stage=main-enter");
        // —— [A] App锁：Storage/Log/Cef 之前 ——（新增块，见 §2）
        if (!TryAcquireAppGuard()) { Environment.ExitCode = SecondInstanceExitCode; return; }

        ConfigureWin7CompatibleTls();
        Application.SetHighDpiMode(...); // 原有不动
        // ...异常兜底原有不动...

        StoragePathManager storageManager; // 原有 try Initialize
        try { storageManager = StoragePathManager.Initialize(); ... }
        catch ... // 原有不动

        // —— [B] Data锁：Storage之后、Cef之前 ——（新增块，见 §3）
        if (!TryAcquireDataGuard(storageManager.CurrentDataRoot)) {
            ShowFatalStartupError("数据目录已被另一实例占用", ...); // 复用原有弹窗函数
            _appGuard?.Dispose(); _appGuard = null;
            return;
        }
        HaloLogger.App.Info($"SingleInstance ok app={_appGuard.MutexName} data={_dataGuard.MutexName} root={storageManager.CurrentDataRoot}");

        // ...CefSettings/Cef.Initialize/Application.Run 原有不动...
        try { Application.Run(new MainForm(...)); }
        finally {
            _dataGuard?.Dispose(); _dataGuard = null; // 新增：先放 data
            _appGuard?.Dispose(); _appGuard = null;   // 新增：再放 app
            HaloLogger.App.Info(...); HaloLogger.Shutdown(); Cef.Shutdown(); // 原有
        }
    }
}
```

行号锚点：`[A]` 插在现 L32（`TraceStartup("stage=main-enter")`）之后、L34（`ConfigureWin7CompatibleTls`）之前；`[B]` 插在现 L72（`HaloLogger.App.Info("HaloAgent starting...")`）之后、`_ = Task.Run(PythonAvailabilitySnapshot)` 之前亦可，但必须在 `Cef.Initialize(L126)` 之前。

## 2. [A] App 锁：探针路径 + 旁路 + 被占分支

```csharp
private static bool TryAcquireAppGuard()
{
    // 2.1 旁路（自动化并行验收用，不记为异常）
    if (string.Equals(Environment.GetEnvironmentVariable("HALO_ALLOW_MULTIINSTANCE"), "1", StringComparison.Ordinal)) {
        TraceStartup("stage=single-instance-bypass reason=HALO_ALLOW_MULTIINSTANCE=1");
        return true;
    }
    // 2.2 探针 bootstrap 路径：只拼接，不写盘（绝不调 StoragePathManager.Initialize）
    string probe;
    try { probe = ResolveBootstrapProbePath(); }
    catch (Exception ex) {
        TraceStartup("stage=single-instance-probe-FAILED: " + ex.Message);
        return true; // 探针失败宁可放行（避免误杀），Data锁兜底
    }
    // 2.3 获取
    SingleInstanceGuard? guard;
    string mutexName;
    try { guard = SingleInstanceGuard.TryAcquireApp(probe, out mutexName); }
    catch (Exception ex) {
        TraceStartup("stage=app-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
        return true; // 获取抛异常宁可放行，Data锁兜底
    }
    if (guard == null) {
        TraceStartup($"stage=app-mutex-null name={mutexName} action=pass-through-to-data-guard");
        return true; // 内部已 trace，回退到 Data 锁
    }
    if (!guard.IsFirstInstance) {
        TraceStartup($"stage=single-instance-blocked scope=app name={mutexName} probe={probe}");
        TryLogWarn($"Second instance blocked (app mutex {mutexName}).");
        NativeWindowActivator.TryBringToFront("Halo-Agent Desktop"); // 04 实现，本篇只调用
        try {
            MessageBox.Show(
                "Halo-Agent 已在运行中，已为你唤起已有窗口。\n\n如需多开：设置环境变量 HALO_ALLOW_MULTIINSTANCE=1，或为第二个实例配置不同的数据目录。",
                "Halo-Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        } catch { /* 无窗口会话（如冒烟）吞掉 */ }
        guard.Dispose();
        return false;
    }
    _appGuard = guard;
    TraceStartup($"stage=single-instance-ok scope=app name={mutexName} probe={probe}");
    return true;
}

private static string ResolveBootstrapProbePath()
{
    var env = Environment.GetEnvironmentVariable("HALO_BOOTSTRAP_PATH");
    if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
    var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.GetFullPath(Path.Combine(lad, "HaloAgent", "bootstrap.json"));
}
```

要点：

* `[A]` 发生在 `HaloLogger` 初始化之前，故提示与诊断一律先 `TraceStartup()`（写 `startup-trace.txt`，`Program.cs:162-173` 原有函数），`TryLogWarn` 内部 try/catch（Logger 未初始化时静默）。
* `MessageBox` 必须在 `return` 之前、`guard.Dispose()` 之前调用（否则老窗口唤起与本窗口释放顺序错乱无影响，但 trace 顺序要求先 blocked 后 dispose）。
* 探针失败/获取抛异常选择“放行”而非“退出”：App 锁是快路径，Data 锁是强兜底，宁可多一次 Data 判定，不可因探针 bug 导致永远无法启动。

## 3. [B] Data 锁：强兜底 + 占用弹窗

```csharp
private static bool TryAcquireDataGuard(string dataRoot)
{
    if (string.Equals(Environment.GetEnvironmentVariable("HALO_ALLOW_MULTIINSTANCE"), "1", StringComparison.Ordinal))
        return true; // 已在 [A] trace，本处静默
    SingleInstanceGuard? guard;
    string mutexName; string lockPath;
    try { guard = SingleInstanceGuard.TryAcquireData(dataRoot, out mutexName, out lockPath); }
    catch (Exception ex) {
        TraceStartup("stage=data-mutex-EX: " + ex.GetType().Name + ":" + ex.Message);
        ShowFatalStartupError("数据目录锁初始化失败", ex); // 复用 Program.cs:208 原有函数
        return false;
    }
    if (guard == null || !guard.IsFirstInstance) {
        TraceStartup($"stage=single-instance-blocked scope=data name={mutexName} root={dataRoot} lock={lockPath}");
        TryLogWarn($"Second instance blocked (data mutex {mutexName}, root {dataRoot}).");
        NativeWindowActivator.TryBringToFront("Halo-Agent Desktop");
        ShowFatalStartupError(
            "数据目录已被另一 Halo-Agent 实例占用",
            new IOException($"DataRoot '{dataRoot}' 已被占用（lock={lockPath}）。请先关闭已有窗口，或更换数据目录后重试。"));
        guard?.Dispose();
        return false;
    }
    _dataGuard = guard;
    TraceStartup($"stage=single-instance-ok scope=data name={mutexName} root={dataRoot} lock={lockPath}");
    return true;
}
```

要点：

* `[B]` 在 `HaloLogger` 初始化之后，故可 `HaloLogger.App.Info/Warn` 直写，同时仍写 `TraceStartup`（`startup-trace.txt` 是无日志闪退唯一入口，铁律保留）。
* 占用弹窗复用 `ShowFatalStartupError(title, ex)`（现 `Program.cs:208-232`），其内部已做 `WriteStartupErrorFile + HaloLogger.Shutdown + MessageBox`，不需另写弹窗。
* `[B]` 失败必须先 `_appGuard?.Dispose()` 再 `return`（见 §1 finally 外的失败分支），否则 App 锁被本进程空占，后续单开被自己拦。

## 4. Trace/日志关键字（冻结，06 断言用）

| 关键字 | 发出点 | 含义 |
|---|---|---|
| `stage=single-instance-bypass` | [A] 旁路 | 本次跳过互斥 |
| `stage=single-instance-ok scope=app/data` | 获取成功 | 正常单开 |
| `stage=single-instance-blocked scope=app/data` | 被占退出 | 第二实例证据 |
| `stage=app-mutex-EX / data-mutex-EX` | 获取抛异常 | 探针/权限 bug 追踪 |
| `mutex-fallback-local` | Guard 内部 Global→Local | Win7 无权回退 |
| `abandoned-acquired` | Guard 内部 | 上次崩溃，自动接管 |

## 5. 不改项清单（review 对照）

* `ConfigureWin7CompatibleTls/DPI/异常兜底/CefSettings/GPU/CORS/wwwroot` 一字不动。
* `StoragePathManager.Initialize()` 签名不动，本篇只读其 `CurrentDataRoot`。
* `MainForm` 构造签名不动（唤起走 04 的 `WndProc`，不加构造参数）。
* 退出码：正常退出保持原语义（`return` 默认 0）；仅“重复实例”用 `42`，供 06 自动化断言 `Process.ExitCode==42`。
