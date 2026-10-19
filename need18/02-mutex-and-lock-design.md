# need18-02 核心设计：SingleInstanceGuard 与双层守卫

> 对应总览：[00-README](./00-README.md)。本篇冻结命名算法与类规约，03/04/05/06 只引用不重定义。

## 1. 设计总览：为什么是“双层”

```text
┌─ App锁（会话级，早拦）────────────────────────────┐
│ key = bootstrap文件路径（未初始化前即可算出）        │
│ 位置：Program.Main() 第一行，Storage/Log/Cef 之前   │
│ 拦住：S1/S2/S4（同bootstrap双击/双跑/并发启动）      │
└───────────────────────────────────────────────────┘
          │ 通过（first）
          ▼
StoragePathManager.Initialize() → activeDataRoot
          │
┌─ Data锁（机器级，兜底）───────────────────────────┐
│ key = activeDataRoot + 常驻 .app.lock 文件句柄      │
│ 位置：Storage初始化后、Cef.Initialize()之前         │
│ 拦住：S3（不同bootstrap但同DataRoot的拷贝/改env）   │
└───────────────────────────────────────────────────┘
          │ 通过
          ▼
Cef.Initialize → Application.Run
```

只做一层不够：

* 只锁 bootstrap → S3 绕过（两个 bootstrap 文件内容指向同一 `C:\HaloAgentData`）。
* 只锁 DataRoot → S4 在 `Initialize()` 并发写 `bootstrap.json` 时已互踩，且报错文案拿不到统一前缀。
* `Mutex` 用 `Local\` 是会话隔离（同用户双击必拦，不同远程会话不误杀）；文件锁是机器级（跨会话同 DataRoot 也拦）。两者互补。

被否决备选（记录以防重提）：

| 备选 | 否决理由 |
|---|---|
| `Process.GetProcessesByName("Host.Desktop")` | `dotnet run` 进程名是 `dotnet`；`CefSharp.BrowserSubprocess.exe` 同名干扰；检查与启动之间有 TOCTOU |
| 瞬时探针文件（建后即删） | 关后即释放，拦不住常驻双活；高频建删触发杀软（`StoragePathManager.cs:728` 实证） |
| NamedPipe 单实例服务 | 过重，本期只需互斥+唤起，不需参数转发 |

## 2. 文件与类规约

### 2.1 新增文件

```text
develop/src/Host.Desktop/Infrastructure/Process/SingleInstanceGuard.cs
develop/src/Host.Desktop/Infrastructure/Process/NativeWindowActivator.cs  // 04 用，可与 Guard 同目录
```

命名空间统一 `Host.Desktop.Infrastructure.Process`，不进 `Storage`（职责：进程互斥，不是路径管理）。

### 2.2 SingleInstanceGuard 公共形状（冻结）

```csharp
namespace Host.Desktop.Infrastructure.Process;

/// <summary>need18：进程排他守卫。Mutex 跨进程 + 文件锁跨会话，持有到 Dispose。</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public string MutexName { get; }
    public string ScopeLabel { get; }      // "app" | "data"，供日志
    public bool IsFirstInstance { get; }
    public string? LockFilePath { get; }   // 仅 data 锁有值

    private Mutex? _mutex;
    private FileStream? _lockFile;
    private bool _disposed;

    // —— 命名（纯函数，可单测，见 §3）——
    public static string BuildAppMutexName(string bootstrapFilePath);
    public static string BuildDataMutexName(string dataRootPath);
    public static string NormalizePathKey(string path);

    // —— 获取（工厂，失败返回 null 或 IsFirstInstance=false，绝不抛到 Main 外）——
    public static SingleInstanceGuard? TryAcquireApp(string bootstrapFilePath, out string mutexName);
    public static SingleInstanceGuard? TryAcquireData(string dataRootPath, out string mutexName, out string lockFilePath);

    public void Dispose(); // 先关文件句柄，再 ReleaseMutex+Dispose
}
```

铁律：

1. `Mutex` 与 `FileStream` 必须由 `Program` 的 `static` 字段持有到进程退出，局部变量会被 GC 回收导致句柄提前释放（.NET `Mutex` 终结器语义）。
2. `TryAcquire*` 内部吞掉可预期异常并转 `TraceStartup` 行（见 03），只在“确实拿不到且不是重复实例”时返回 `null`（如路径非法），调用方按“失败即退出”处理。
3. `AbandonedMutexException` 视为**获取成功**（上次崩溃遗留），记 `Warn`，不弹框（见 05）。

### 2.3 获取流程状态机

```text
TryAcquireApp(bootstrapPath):
  name = BuildAppMutexName(bootstrapPath)
  try mutex = new Mutex(initiallyOwned:true, name, out createdNew)
  catch AbandonedMutexException → mutex=已持有, createdNew:=true, trace abandoned-acquired
  catch UnauthorizedAccessException → 重试 Local\ 前缀（Global→Local 回退）, 再失败返回 null
  catch IOException/UnauthorizedAccessException(目录不可写无关,Mutex在对象管理器) → 返回 null
  return Guard{IsFirstInstance=createdNew}

TryAcquireData(dataRoot):
  name = BuildDataMutexName(dataRoot)
  mutex 同上（createdNew==false → 直接返回 Guard{IsFirst=false}，不碰文件）
  lockPath = Path.Combine(dataRoot, ".app.lock")
  try lockFile = new FileStream(lockPath, Create, Write, Share.None, 4096, UseAsync)
      + 写入 $"pid={Environment.ProcessId};startUtc={DateTime.UtcNow:O};mutex={name};\n" + Flush(true)
  catch IOException/UnauthorizedAccessException → 释放mutex, 返回 Guard{IsFirst=false}
  return Guard{IsFirst=true, LockFilePath=lockPath}
```

注意顺序：data 锁先拿 Mutex 再开文件，避免两个进程同时 `Create` 同一 `.app.lock` 的竞态由文件语义承担（`Share.None` 第二个直接 `IOException`，天然串行）。

## 3. 命名算法（冻结，单测锁定）

```csharp
public static string NormalizePathKey(string path)
{
    if (string.IsNullOrWhiteSpace(path)) return string.Empty;
    var full = Path.GetFullPath(path.Trim());
    full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return full.ToLowerInvariant(); // Windows 不区分大小写；Linux 跑单测也不误判（仅测试用）
}

static string Hash16(string normalizedKey)
{
    using var sha = SHA256.Create();
    var bytes = Encoding.UTF8.GetBytes("halo-agent-v1|" + normalizedKey);
    return Convert.ToHexString(sha.ComputeHash(bytes))[..16].ToLowerInvariant();
}

public static string BuildAppMutexName(string bootstrapFilePath)
    => @"Local\HaloAgent-App-" + Hash16(NormalizePathKey(bootstrapFilePath));

public static string BuildDataMutexName(string dataRootPath)
    => @"Local\HaloAgent-Data-" + Hash16(NormalizePathKey(dataRootPath));
```

* 前缀 `Local\`：会话隔离，同用户双击必拦，不同用户会话各跑各的（服务器场景不误杀）。
* `Global\` 尝试：`TryAcquire*` 内部先试 `Global\` 同名，`UnauthorizedAccessException` 即回退 `Local\`（Win7 非管理员无 `SeCreateGlobalPrivilege`）。回退必须记 trace `mutex-fallback-local`。
* 固定盐 `halo-agent-v1|`：防与其他软件哈希碰撞；改盐 = 改名，必须同步改 06 命名单测。
* 示例：`C:\HaloAgentData` → `Local\HaloAgent-Data-9f2c…`（16 hex，实际值由单测快照锁定，不手算）。

## 4. 生命周期与释放顺序

```text
Program.static _appGuard, _dataGuard
  Main() 获取 → Application.Run → finally{
    _dataGuard?.Dispose();  // 先放 data（文件句柄关 → 后来者可进）
    _appGuard?.Dispose();   // 再放 app
    HaloLogger.Shutdown();  // 原有
    Cef.Shutdown();         // 原有
  }
```

`Dispose()` 幂等，可重入，吞异常（关闭期不弹框，只 trace）。

## 5. 杀软与 Win7 相容性说明

* 文件锁是**一次性常驻打开**（`Create+Share.None` 一次，持有到退出），不是轮询建删，故不触发 `StoragePathManager.cs:728` 所述勒索软件启发式（该注释针对高频探针写）。
* `.app.lock` 内容只写一行诊断文本，不加密，不含 Key；进程退出 OS 自动释放文件锁，无残留（`AbandonedMutex` 同理）。
* 全程 BCL：`Mutex/SHA256/FileStream/DateTime`，无 P/Invoke（P/Invoke 只在 04 唤起器，不在本类）。
* `net6.0-windows` + CefSharp 109 冻结，不新增 PackageReference。
