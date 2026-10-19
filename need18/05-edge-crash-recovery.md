# need18-05 边界与恢复：崩溃/权限/同根/杀软/旁路

> 对应总览：[00-README](./00-README.md)。本篇是 02/03 的异常分支展开，全部要在 06 有对应用例。

## 1. 崩溃恢复（核心卖点：无残留）

| 崩溃点 | OS/CLR 行为 | 本方案行为 | 用户感知 |
|---|---|---|---|
| `kill -9` / 任务管理器结束任务 / 断电 | `Mutex` 被 OS 标记 abandoned；`.app.lock` 文件句柄被 OS 关闭 | 下次 `new Mutex(true,name,out created)` 抛 `AbandonedMutexException` → catch 后视为成功，trace `abandoned-acquired`，记 Warn；`.app.lock` 重新 `Create` 覆盖 | 直接启动成功，无需删锁 |
| `Cef.Initialize` 中崩溃 | 同上（App 锁已持有，Data 锁未持有） | App 锁 abandoned 接管，Data 锁文件可重建 | 同上 |
| `Application.Run` 中未捕获异常 | 先走 `Program.cs:42-51` 兜底写 `startup-error.txt`，再进程退出，锁语义同 kill | 同上 | 同上 |

实现要求（`SingleInstanceGuard` 内）：

```csharp
try { _mutex = new Mutex(true, name, out createdNew); }
catch (AbandonedMutexException ex) {
    // 此时当前线程已持有该 Mutex（CLR语义），不可再 Release 两次
    _mutex = ex.Mutex ?? Mutex.OpenExisting(name);
    createdNew = true;
    abandoned = true;
}
```

`Dispose()` 必须能处理 `abandoned` 态（正常 `ReleaseMutex` 即可，不需特殊分支）。

反模式禁令：**禁止**用“启动时删 `.app.lock`”做恢复（那会把正常运行的第一个实例的锁删掉，造成双活）。恢复只能靠 OS 语义，不靠手动删文件。

## 2. 权限：Global→Local 回退

```text
TryAcquire*(name=Local\...):
  先试 Global\HaloAgent-...（跨会话强互斥，可选）
    └─ UnauthorizedAccessException（Win7 非管理员无 SeCreateGlobalPrivilege）
       → trace mutex-fallback-local → 用 Local\ 重试
    └─ 成功 → 用 Global（服务器多会话也互斥，更强）
  若一开始就用 Local\（默认），无此分支。
```

默认策略：**直接用 `Local\`**（02 §3 定值），不先试 Global。理由：桌面单用户场景 Local 已够；先试 Global 会在无权机器上多一次异常开销且 trace 噪音。只有当未来要做“服务器多会话互斥”时才翻转顺序，届时改 02 §3 + 单测。

## 3. 同 DataRoot 不同 bootstrap（S3）

成因：用户拷贝 `bootstrap.json` 改 `DataRootPath` 指向同一目录，或 `HALO_BOOTSTRAP_PATH` 切了但 `DataRoot` 配重了。

行为：App 锁放行（命名空间不同，各锁各的）→ `StoragePathManager.Initialize()` 正常算出同一 `activeDataRoot` → Data 锁 `Mutex + .app.lock(Share.None)` 第二个 `IOException` → 走 03 §3 占用弹窗 + 退 42 + trace `single-instance-blocked scope=data`。

`.app.lock` 内容供排错：

```text
pid=12345;startUtc=2026-10-19T08:00:00Z;mutex=Local\HaloAgent-Data-9f2c...;hostname=PC-01;
```

第二个实例的弹窗文案带 `lock=` 路径，用户可凭 pid 去任务管理器核对。**禁止**在 `.app.lock` 写任何 Key/Token（01 §4 相容性）。

## 4. 杀软与文件锁频率

* `StoragePathManager.cs:720-733` 实证：高频 `FileShare.None + WriteThrough` 探针会被误判勒索软件。本方案文件锁一生只 `Create` 一次、持有到退出，无轮询，故风险极低。
* 若某杀软连一次性 `Share.None` 常驻也拦（`UnauthorizedAccessException`），`TryAcquireData` 降级为“仅 Mutex”模式：trace `datalock-file-fallback-mutex-only` 后仍视为成功（Mutex 已能拦同会话双开；跨会话极端场景接受降级）。此分支必须有单测（mock `FileStream` 抛异常）或手工记录。

## 5. 旁路与多开矩阵

| env | bootstrap | DataRoot | 行为 |
|---|---|---|---|
| `HALO_ALLOW_MULTIINSTANCE=1` | 任意 | 任意 | 跳过 App+Data 锁，trace `single-instance-bypass`，双活（后果自负：历史可能互踩，属用户显式选择） |
| 未设 | 相同 | 相同 | 第二个 App 锁处退 42（S1/S2/S4） |
| 未设 | 不同 | 相同 | 第二个 Data 锁处退 42（S3） |
| 未设 | 不同 | 不同 | 并存（F3，如 dev 双根调试） |
| `HALO_AUTOVERIFY_OUTPUT` 并行冒烟 | 相同 | 相同 | 必须配 `HALO_ALLOW_MULTIINSTANCE=1` 或各用隔离 `HALO_BOOTSTRAP_PATH`+隔离 DataRoot，否则第二路冒烟会被拦 |

## 6. 并发启动竞态（S4）

A/B 同时进 `Main()`：`new Mutex(true,name,out createdNew)` 是内核原子操作，有且仅有一个 `createdNew==true`。败者走 blocked 分支，无 TOCTOU。文件锁同理（`Share.None` 第二个直接 `IOException`）。故**禁止**在 Mutex 外再加 `Thread.Sleep` 重试循环（那是掩盖 bug）。

## 7. 退出码与诊断文件

* 重复实例退出码 `42`（03 §5 冻结）；正常退出 0；存储初始化失败走原有 `ShowFatalStartupError`（返回 void，退出码保持 0，由 MessageBox 语义决定，不强求）。
* 诊断三件套：`startup-trace.txt`（必有 blocked/ok 行）+ `startup-error.txt`（Data 占用时 `ShowFatalStartupError` 写）+ `DataRoot/.app.lock`（首实例常驻，退出后可删）。
