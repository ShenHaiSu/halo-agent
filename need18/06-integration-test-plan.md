# need18-06 验收计划：Stage18 单测 + 手工矩阵 + 门禁

> 对应总览：[00-README](./00-README.md)。本篇是唯一准出依据，02-05 的每个冻结项在此都有用例。

## 1. 新增测试文件规约

```text
develop/tests/HaloAgent.Tests/Stage18SingleInstanceTests.cs
```

* 只测 `Infrastructure/Process` 纯逻辑 + `Program` 探针路径拼接（可抽 `internal static ResolveBootstrapProbePath` 供 `InternalsVisibleTo`，或把拼接逻辑下沉到 Guard 的 `ResolveBootstrapProbePath()` 纯函数，本方案推荐后者）。
* 不启动 `Cef` / 不碰 `C:\HaloAgentData` / 不弹 `MessageBox`（唤起器单测只测命名与 P/Invoke 参数构造，不调真实 `FindWindow`；或用 `HALO_ALLOW_MULTIINSTANCE` 隔离）。
* 全部用 `Path.GetTempPath()` 隔离目录，`Mutex` 名加 `TestRun-<Guid>` 后缀防与本机运行实例冲突（测试专用重载 `BuildAppMutexName(path, saltSuffix)`，生产调用不传）。

## 2. 单测清单（至少 10 个，命名冻结）

| # | 用例名 | 断言 | 对应 |
|---|---|---|---|
| T1 | `Normalize_SamePath_DifferentCase_Equal` | `NormalizePathKey("C:\\HaloAgentData")==NormalizePathKey("c:\\haloagentdata\\")` | 02 §3 |
| T2 | `MutexName_Stable_And_Prefixed` | 两次 `BuildAppMutexName` 相等；以 `Local\HaloAgent-App-` 开头；hex 长 16 | 02 §3 |
| T3 | `AppAndData_Namespace_Isolated` | 同路径的 App 名 ≠ Data 名 | 02 §1 |
| T4 | `SecondAcquire_SameProcess_IsNotFirst` | 同名 `TryAcquireApp` 两次，第二次 `IsFirstInstance==false`，后 Dispose | 02 §2 |
| T5 | `AbandonedMutex_CanReacquire` | 线程持有 Mutex 后 Abort（或直接抛场景模拟），新 `TryAcquireApp` 成功且 trace `abandoned-acquired` | 05 §1 |
| T6 | `DataLock_SecondFails_ThenSucceedsAfterDispose` | `GuardA=TryAcquireData(tmp)` 成功；`GuardB` 失败；`A.Dispose()` 后 `GuardC` 成功 | 02 §2 |
| T7 | `DataLock_FileContent_HasPid` | `.app.lock` 首行含 `pid=` 且 pid==当前进程 | 05 §3 |
| T8 | `ProbePath_EnvOverride_Wins` | 设 `HALO_BOOTSTRAP_PATH` 后 `ResolveBootstrapProbePath()==env`；清掉后回 `LocalAppData\HaloAgent\bootstrap.json` | 03 §2 |
| T9 | `BypassEnv_Skips_All` | 逻辑断言：当 env==1 时 `TryAcquireAppGuard` 不建 Mutex（以可测的 `ShouldBypass()` 纯函数断言，避免调真实 Main） | 05 §5 |
| T10 | `Dispose_IsIdempotent` | `Dispose();Dispose();` 不抛 | 02 §4 |
| T11（可选） | `FileLockDenied_FallsBack_MutexOnly` | mock/只读目录下 `TryAcquireData` 走 `datalock-file-fallback-mutex-only` 仍成功 | 05 §4 |

T1-T10 为合闸必备，T11 若 mock 成本高可转手工记录。

## 3. 手工矩阵（发布前必跑，Win10/11 + Win7 各一遍）

| # | 步骤 | 期望 | 取证 |
|---|---|---|---|
| M1 | `build-dist.cmd` 后双击 `dist\Host.Desktop.exe` 两次 | 第二个弹“已在运行”+ 老窗口前置 + 秒退 | 目视 + `echo %ERRORLEVEL%`==42 |
| M2 | `type startup-trace.txt \| findstr single-instance-blocked` | 第二实例行命中 `scope=app` | trace 文件 |
| M3 | `taskkill /F /IM Host.Desktop.exe` 后再双击 | 一次启动成功，无“被占用” | 目视 |
| M4 | 拷一份 `bootstrap.json` 改 `HALO_BOOTSTRAP_PATH` 指向它但 `DataRoot` 相同，双开 | 第二个弹“数据目录被占用” | trace `scope=data` |
| M5 | `set HALO_ALLOW_MULTIINSTANCE=1` 后双开 | 双活并存 | 目视（事后关掉） |
| M6 | 最小化首窗口后双击 | 首窗口还原+前置 | 目视（04 L1/L2） |
| M7 | 首实例正常退出后 `dir DataRoot\.app.lock` | 文件可删/已被释放（无句柄占用） | `del` 成功或文件不存在 |

## 4. 日志关键字总表（grep 字典，冻结）

```text
stage=main-enter
stage=single-instance-bypass
stage=single-instance-ok scope=app
stage=single-instance-ok scope=data
stage=single-instance-blocked scope=app
stage=single-instance-blocked scope=data
mutex-fallback-local
abandoned-acquired
datalock-file-fallback-mutex-only
activate-ok / activate-failed
```

门禁：`findstr /c:"single-instance" startup-trace.txt` 在任何一次启动后非空（旁路/成功/被拦三者必居其一）。

## 5. 门禁与回滚

```cmd
:: 后端（本期唯一代码门禁）
dotnet build HaloAgent.sln -c Debug
dotnet test HaloAgent.sln -c Debug
:: 前端不受影响（本期不动 Web.Client），仍建议跑
pnpm --dir src/Web.Client exec vue-tsc -b
```

* 合闸条件：T1-T10 全绿 + M1-M7 在 Win10/11 通过（Win7 可延后一轮，但 M1/M3 不可缺）。
* 回滚：只动 `Program.cs` 双调用点 + 2 新增文件，任一门禁红灯直接 revert `Program.cs` 两块 + 不发布 Guard 文件即可回滚到“无互斥”现状，零数据迁移。
* 灰度：首版默认强互斥 + 旁路 env；若用户反馈“多用户服务器需并存”，再按 05 §2 翻转 Global/Local 策略，不在本期做。

## 6. 工作量与顺序（供排期）

| 任务 | 文件 | 量级 |
|---|---|---|
| Guard+命名+单测 | `Infrastructure/Process/SingleInstanceGuard.cs` + `Stage18SingleInstanceTests.cs` | ~150 + ~150 行，半天 |
| Program 双点接入 | `Program.cs`（[A][B]+探针+退出码） | ~60 行，1h |
| 唤起器+WndProc | `NativeWindowActivator.cs` + `MainForm.cs` 20 行 | ~80 行，1h |
| 手工矩阵+文档 | M1-M7 + 本系列定稿 | 半天 |

合计约 1.5 人天，P0（Guard+Program）可独立合闸先止血，04/05 随后跟上。
