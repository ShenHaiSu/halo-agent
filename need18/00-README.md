# need18 总览：进程排他启动 — 防止双开 Agent 互踩

> 状态：施工方案（待实现）。前置：`develop/src/Host.Desktop/Program.cs`、`MainForm.cs`、`Infrastructure/Storage/StoragePathManager.cs`、`Infrastructure/Storage/AtomicFileWriter.cs`、`Infrastructure/Logging/HaloLogger.cs`。
> 目标：同一用户会话 + 同一数据根只允许 1 个 `Host.Desktop.exe` 存活，第二个实例在进 `Storage/Cef` 之前就提示 + 唤起老窗口 + 退出。
> 范围：只改启动链路（`Program` + 新增 `Infrastructure/Process` + `MainForm` 唤起）。不动 Agent/工具/网关语义，不做数据迁移，不改落盘算法。

## 1. 问题地图（症状 → 根因 → 方案文档 → 代码锚点）

| # | 症状 | 根因 | 方案文档 | 代码锚点 |
|---|---|---|---|---|
| 1 | 双击两次 `Host.Desktop.exe` 起两个主窗口，各自可发 Agent | `Program.Main()` 无任何 `Mutex/SingleInstance` 检查，直接 `Run` | [01](./01-requirements-analysis.md)、[02](./02-mutex-and-lock-design.md) | `develop/src/Host.Desktop/Program.cs:29-148`、`Host.Desktop.csproj` |
| 2 | 双进程并发写 `meta.json/messages.jsonl`，后写者覆盖，历史丢失 | `AtomicFileWriter.WriteJsonAtomic*: File.Move(overwrite:true)` + 临时文件 `FileShare.None` 只锁自己 | [02](./02-mutex-and-lock-design.md)、[05](./05-edge-crash-recovery.md) | `Infrastructure/Storage/AtomicFileWriter.cs:36-120` |
| 3 | 双进程并发 `AppendMessageAsync`，同一 `sessionId` 两条 Agent 流交错 | `SessionRepository._sessionLocks/_workspacesIndexLock: SemaphoreSlim` 只是进程内内存锁 | [01](./01-requirements-analysis.md) | `Infrastructure/Storage/SessionRepository.cs:54-56,158-489` |
| 4 | `SESSION_BUSY` 拦不住第二个进程的生成/压缩 | `HostBridgeFacade._activeSessionTokens: ConcurrentDictionary` 进程内可见，跨进程不可见 | [01](./01-requirements-analysis.md) | `Bridge/HostBridgeFacade.cs:38`、`Bridge/HostBridgeFacade.History.cs:9-15,34-35` |
| 5 | 双 `Cef.Initialize` 争用同一 `cef-cache/`，可能锁错/崩溃 | 互斥点放在 `Cef.Initialize` 之后就太晚 | [03](./03-program-integration.md) | `Program.cs:80-134`、`StoragePathManager.CefCacheDirectory` |
| 6 | 崩溃后残留锁导致下次无法启动 | 若用“建后即删的探针文件/注册表 flag”会留脏 | [05](./05-edge-crash-recovery.md) | 新增 `SingleInstanceGuard`（`AbandonedMutexException` 语义） |
| 7 | 不同 `bootstrap` 指向同一 `DataRoot`（拷贝目录/改 env）绕过单锁 | 只锁 `bootstrapPath` 不够，需二级锁 `DataRoot` | [02](./02-mutex-and-lock-design.md)、[05](./05-edge-crash-recovery.md) | `StoragePathManager.Initialize():114-246` |

全仓证据（2026-10-19 实测）：`grep -ri "Mutex|SingleInstance|GetProcessesByName|already.*running" develop/src/Host.Desktop` → **0 命中**（除进程内 `lock()` 外）。`csproj` 无 `SingleInstance`。即当前**无排他性进程启动检测**。

## 2. 施工分期与硬顺序

```text
P0 先行（必做）：02 互斥核心 + 03 Program 接入
  └─ 准出：双开第二个进程秒退（<1s），不进 Cef；startup-trace.txt 留 single-instance-blocked。

P1 紧接：04 老窗口唤起（FindWindow + WM_SHOWME）
  └─ 准出：第二实例退出前老窗口前置/还原，无“点了没反应”错觉。

P2 收尾：05 边界恢复 + 06 测试验收矩阵
  └─ 准出：杀进程/断电后可正常启动；同 DataRoot 不同 bootstrap 被拦；HALO_ALLOW_MULTIINSTANCE=1 可旁路。
```

**硬约束**：02 的命名算法（归一化 + SHA256 取 16HEX + `Local\` 前缀）是 03/04/05/06 的前提，先冻结 02 §3，后续只追加不改名；03 必须保证互斥点在 `StoragePathManager.Initialize()` 之前（App 锁）与 `Cef.Initialize()` 之前（Data 锁）；任何改动不得新增 NuGet，不得升 `net6.0-windows` / `CefSharp 109.1.110`。

## 3. 兼容承诺（冻结，全期有效）

1. 单实例行为不变：正常单开启动链路零感知，只多两行 `Info` 日志 + 一个常驻 `Mutex` 句柄 + 一个常驻 `.app.lock` 句柄。
2. 不改存储格式：不改 `bootstrap.json/meta.json/jsonl` 结构，不改 `AtomicFileWriter` 替换语义，不改 `SemaphoreSlim` 进程内锁。
3. 崩溃可恢复：任何 `kill -9` / 断电后下次启动必须成功，不需手动删锁（靠 `AbandonedMutexException` + 文件锁 OS 自动释放）。
4. 可旁路：`HALO_ALLOW_MULTIINSTANCE=1` 跳过全部互斥（供自动化并行验收 `HALO_AUTOVERIFY_OUTPUT` 与开发者双数据根调试）；`HALO_BOOTSTRAP_PATH` 指向不同且 `DataRoot` 不同时天然允许并存。
5. Win7 铁律：只用 .NET 6 BCL（`Mutex/SHA256/FileStream/FindWindow P/Invoke`），`Global\` 无权自动回退 `Local\`，绝不抛到用户面前。

## 4. 文档索引

| 文档 | 内容 | 预计改动 |
|---|---|---|
| [01](./01-requirements-analysis.md) | 需求分析：场景/风险/非目标/验收标准 | —（只读） |
| [02](./02-mutex-and-lock-design.md) | 核心设计：`SingleInstanceGuard` 类规约、双层守卫、命名算法、状态机 | 1 新增源码 |
| [03](./03-program-integration.md) | `Program.cs` 接入：探针路径、双获取点、错误弹窗、Trace、释放顺序 | 1 存量修改 |
| [04](./04-mainform-activation.md) | 窗口唤起：`NativeWindowActivator` + `MainForm.WndProc WM_SHOWME` | 1 新增 + 1 存量修改 |
| [05](./05-edge-crash-recovery.md) | 边界与恢复：崩溃/无权/同根不同指/杀软/旁路 | 含 02/03 的异常分支 |
| [06](./06-integration-test-plan.md) | 验收：`Stage18SingleInstanceTests` 清单、手工矩阵、日志断言、回滚 | 1 测试新增 |

## 5. 施工纪律（所有文档通用）

* 每个源码改动独立可 review，合入前 `dotnet build HaloAgent.sln -c Debug` + `dotnet test HaloAgent.sln -c Debug` 全绿，前端 `vue-tsc -b && vite build` 不受影响（本期不动前端）。
* 任何 Mutex 名改动必须同步改 02 §3 + 06 命名稳定性单测，不许各写一套。
* 任何新增 Trace/日志关键字必须：① 进 06 §4 关键字表；② 单测或手工断言 `startup-trace.txt` 可 grep 到。
* 测试不得依赖本机已有 `C:\HaloAgentData`，一律用 `Path.GetTempPath()` 隔离目录 + `HALO_ALLOW_MULTIINSTANCE` 隔离。
