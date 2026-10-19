# need18-01 需求分析：为什么必须做进程排他启动

> 对应总览：[00-README](./00-README.md)。本篇只讲“是什么/为什么/验什么”，不讲“怎么做”（做法见 02/03/04）。

## 1. 背景与现状证据

### 1.1 启动链路现状（逐行取证）

`develop/src/Host.Desktop/Program.cs:29-148` 全量如下（摘要）：

```csharp
[STAThread]
static void Main()
{
    TraceStartup("stage=main-enter");                       // L32
    ConfigureWin7CompatibleTls();                           // L34
    Application.SetHighDpiMode(...);                        // L37-39
    Application.ThreadException += ...;                     // L42-46（仅异常兜底）
    AppDomain.CurrentDomain.UnhandledException += ...;      // L47-51（仅异常兜底）
    storageManager = StoragePathManager.Initialize();       // L59（直接初始化，无锁）
    HaloLogger.Initialize(storageManager);                  // L61
    var settings = new CefSettings { CachePath = storageManager.CefCacheDirectory, ... }; // L80-87
    Cef.Initialize(settings, ...);                          // L126（直接初始化）
    Application.Run(new MainForm(storageManager, gpuDisabled)); // L139（直接Run）
}
```

`develop/src/Host.Desktop/MainForm.cs:24-87`：构造函数直接 `new ChromiumWebBrowser + new HostBridgeFacade + Task.Run(ScanAndHealAllSessions)`，无任何“是否已有实例”判断。

`develop/src/Host.Desktop/Host.Desktop.csproj`：无 `SingleInstance/ApplicationInstance` 相关 MSBuild 属性（已通读全文件，仅 `WinExe/net6.0-windows/CefSharp 109.1.110`）。

### 1.2 全仓无互斥的检索证据

2026-10-19 在 `develop/src/Host.Desktop` 执行（`cmd` 因 `findstr` 多模式退出码问题改用 Python 正则全扫）：

```text
pattern = mutex|single.?instance|GetProcesses|FileShare.None|lock\s*\(|Monitor.|Semaphore|NamedPipe|already.*running|second.*instance|duplicate
HITS=114，其中与进程排他相关的 = 0
```

114 条命中全部归类如下，无一是跨进程互斥：

| 类别 | 证据 | 为什么不算排他 |
|---|---|---|
| 进程内 `lock()` | `JsEventDispatcher.cs:29 lock(TimerLock)`、`AgentConfigService.cs:27 lock(_syncLock)` 等 ~40 处 | `lock` 只锁本进程对象 |
| 进程内 `SemaphoreSlim` | `SessionRepository.cs:54-56 ConcurrentDictionary<string,SemaphoreSlim> _sessionLocks`、`WorkspaceSessionService.cs:57 _sessionAppendLocks` | 内存字典，第二个进程有独立一份 |
| 进程内 `ConcurrentDictionary` | `HostBridgeFacade.cs:38 _activeSessionTokens` | 同上，`SESSION_BUSY` 判定 `HostBridgeFacade.History.cs:9-15 ContainsKey("agent:"+sid)` 跨进程不可见 |
| 文件原子写（非锁） | `AtomicFileWriter.cs:54-68 FileShare.None(临时文件)+File.Move(overwrite:true)` | `None` 只锁 `*.tmp` 瞬间，最终覆盖是 last-writer-wins |
| 注释明确禁用排他 | `StoragePathManager.cs:728 // 严禁使用 FileShare.None / FileOptions.WriteThrough 等排他式直写标志` | 该处是写探针防杀软误报，不能引申为已有排他 |

结论：**当前无排他性进程启动检测，双开即双活。**

### 1.3 双开互踩的五条因果链

1. **存储覆盖**：A/B 同写 `sessions/<ws>/<sess>/messages.jsonl` → `AtomicFileWriter` 各写各的 `*.tmp` → 先后 `Move` → 后者覆盖前者，前者追加丢失。`meta.json/index` 同理。
2. **Agent 双跑扣费**：同一 `sessionId` 在 A/B 各有一个 `AgentOrchestrator` + `CancellationTokenSource`，`SESSION_BUSY` 查的是各自 `_activeSessionTokens`，互相看不见 → 同一用户问题调两次 LLM。
3. **愈合竞争**：A/B 启动各跑一次 `ScanAndHealAllSessions()`，对同一脏会话各补一条 `synthetic cancelled tool`，产生重复愈合记录。
4. **CEF 缓存争用**：A/B 共用 `CurrentDataRoot/cef-cache`，双 `Cef.Initialize` 并发打开同一 `CachePath`，Chromium 锁文件语义下轻则第二个报锁错，重则缓存损坏。
5. **`usage.db` WAL 竞争**：`metrics/usage.db`（SQLite，见 `Microsoft.Data.Sqlite 6.0.28`）双进程并发写，WAL 恢复与锁等待不可控（本期不改 DB，只靠互斥根治）。

## 2. 场景清单（必须拦 / 必须放）

### 2.1 必须拦（P0）

| # | 场景 | 期望行为 |
|---|---|---|
| S1 | 用户双击两次 `dist\Host.Desktop.exe`（同一 bootstrap，同一 DataRoot） | 第二个进程弹“已在运行”+ 唤起第一个窗口 + 秒退（<1s），`startup-trace.txt` 留 `single-instance-blocked` |
| S2 | 用户 `dotnet run` + 双击 exe 同时跑（同一 DataRoot） | 同 S1，进程名不同也必须拦（故不能用进程名枚举） |
| S3 | 同一会话内 `HALO_BOOTSTRAP_PATH` 不同但 `DataRoot` 相同（拷贝 bootstrap/改 env） | 第二个在 Data 锁处被拦，弹“数据目录被占用”+ 退出，不进 Cef |
| S4 | 启动瞬间双击（A 还在 `Cef.Initialize`，B 已进 `Main`） | B 在 App 锁处即被拦，不与 A 并发初始化 |

### 2.2 必须放（P0 回归）

| # | 场景 | 期望行为 |
|---|---|---|
| F1 | 正常单开 | 零感知启动，`startup-trace.txt` 多 `single-instance-ok`，功能全可用 |
| F2 | `HALO_ALLOW_MULTIINSTANCE=1`（自动化并行验收/开发者双根调试） | 跳过全部互斥，双开并存（后果自负），日志留 `multi-instance-bypass` |
| F3 | `HALO_BOOTSTRAP_PATH` 指向不同且解析后 `DataRoot` 也不同 | 天然两个命名空间，允许并存（各锁各的） |
| F4 | 前一进程已崩溃（任务管理器杀掉/`kill -9`/断电） | 下次启动必须成功，不需手动删锁 |

### 2.3 非目标（本期不做）

* 单实例参数转发（第二实例把文件路径/URL 传给第一实例打开）：本期第二实例只唤起+退出，不转发。
* 跨用户会话互斥（`Global\` 强互斥）：默认 `Local\`（会话隔离）+ 文件锁（机器级）组合已够用，不做跨会话强杀。
* 前端“双标签页”互斥：前端是单 Browser 单 Bridge，不存在双前段竞争，不做。
* DB 级多写者合并：靠互斥根治，不在 SQLite 层做合并队列。

## 3. 验收标准（可执行断言）

| ID | 断言 | 取证位置 |
|---|---|---|
| AC1 | 双开时第二个进程在 1s 内退出，且退出码为约定的 `42`（或 0，见 03 定值） | 任务管理器/手工秒表 + `echo %ERRORLEVEL%` |
| AC2 | 第二个进程退出前有可见提示（MessageBox），且老窗口被前置/还原 | 目视 + 04 唤起日志 |
| AC3 | `startup-trace.txt` 可 `findstr single-instance-blocked` 命中第二实例行 | `AppDomain.BaseDirectory/startup-trace.txt` |
| AC4 | 杀掉第一个进程后（`taskkill /F /IM Host.Desktop.exe`），可立即单开成功 | 手工 + 05 恢复矩阵 |
| AC5 | `HALO_ALLOW_MULTIINSTANCE=1` 时双开并存 | env 旁路测试 |
| AC6 | `dotnet test` 全绿，前端构建不受影响 | 06 门禁 |

## 4. 与既有约束的相容性

* Win7 SP1 + .NET 6 BCL only：只用 `System.Threading.Mutex`、`SHA256`、`FileStream`、`user32.dll P/Invoke`，不新增 NuGet（`csproj` 冻结）。
* 杀软红线：`StoragePathManager.cs:720-733 TryEnsureDirectoryWritable` 已实证高频 `FileShare.None` 探针会被误判为勒索软件，故本方案文件锁只做**一次常驻打开**，不做轮询创建删除（详见 02 §5）。
* 日志铁律：启动期 `HaloLogger` 可能尚未初始化，所有互斥分支必须先 `TraceStartup()` 写 `startup-trace.txt`，再 `TryLog`（见 03）。
