# need22-01a P0 安全诊断与代码证据

> 只摆证据，不给方案。路径前缀 `develop/src/Host.Desktop/`，行号以当前快照为准。

## 1. 路径安全：越界只警告不拒绝（高风险）

| 位置 | 现状 | 后果 |
|---|---|---|
| `Tools/Safety/PathSafetyValidator.cs` → `ValidateWrite` | 计算出 `OutsideWorkspace=true` 仍返回 `Allowed=true` + `Warning` | 调用方自行决定，实际全部放行 |
| `BuiltIn/WriteFileTool.cs:117-118,315` | 收到越界结果仅记警告，继续写盘 | `../` 可写工作区外任意文件 |
| `BuiltIn/EditFileTool.cs` | 直接弃元 `_ = ValidateWrite(...)`，连警告都不看 | 同上，且无审计 |
| `BuiltIn/ReadFileTool.cs`、`ListDirTool.cs`、`GrepSearchTool.cs`、`Tools/FileToolShared.cs:ResolveReadPath/ResolveWorkspacePath` | 允许绝对路径任意读，仅 `Path.GetFullPath` 规范化 | 可读 `C:\Windows\...`、用户目录敏感文件 |
| 全局 | 无 symlink/junction 解析（`GetFinalPathNameByHandle` 未用） | 先建软链后穿越，校验形同虚设 |

复现：`write_file(path="../../evil.txt")` → 返回 `success + warning`，文件实际落盘。

## 2. 命令执行直透（高风险）

| 位置 | 现状 |
|---|---|
| `Tools/Process/CommandRunnerProbe.cs:51` | `cmd.exe /d /s /c "command"` 字符串拼接直透 |
| `BuiltIn/ExecCommandTool.cs` | 无命令黑白名单、无长度上限；`workdir` 可指向工作区外；`ComSpec` 取环境变量（可被劫持）；`timeout_ms=0` 视为无限等待 |
| `Tools/Safety/*` | 无命令级审计钩子（`IToolHook` 定义了但 `DispatchAsync` 未接入） |

复现：`exec_command(command="...", workdir="C:\\Windows\\System32", timeout_ms=0)` 全部通过。

## 3. 参数校验缺口（中风险）

| 位置 | 现状 |
|---|---|
| `Tools/Validation/ToolCallValidator.cs:196` | 未知工具直接透传，不拒绝 |
| `edit_lines` | 缺 `limit` 参数，靠业务层兜底，校验与执行双轨 |
| `BuiltIn/SkillTool*.cs` + `ToolSchemaBuilder.cs` | Schema 声明仅 `list\|get`，实现含 `create/update/delete`，合法写必被 `ENUM_VIOLATION` 误杀，逼用户绕过校验 |
| `BuiltIn/GoalTool.cs` | 声明 `get\|set`，描述含 `update/progress/...`，同上 |
| 各 `Handler.ExecuteAsync` | 重复手写 `JsonDocument.Parse`，与 `NormalizedJson` 双轨，易漂移 |

## 4. 备份/撤销不可靠（中风险）

| 位置 | 现状 |
|---|---|
| `BuiltIn/BackupStore.cs:WriteBackup` | `File.Copy` 非原子、无 fsync、无全局配额 |
| `BackupStore.Prune` | 按文件名字典序删旧，非 `mtime`，新备份可能被误删 |
| `BuiltIn/UndoEditTool.cs:GuessOriginal` | 按 `_` 切分 basename 猜原名，`my_file_v2.cs` 类文件名歧义大 |
| 恢复路径 | 手写 tmp+Move，各处实现不一致 |

## 5. 敏感信息落地（中风险）

| 位置 | 现状 |
|---|---|
| `Tools/Safety/ToolOutputGuard.cs:SanitizeFileName` | 仅过滤非法字符，`callId="../../x"` 可逃逸 `tool-outputs/` 目录 |
| spill/备份/memory | 明文落盘，无 `sk-/Bearer/secret` 扫描，无清理任务 |
| `Infrastructure/Storage/SecretProtector.cs:MaskSecret` | 保留首尾各 4 字符，`sk-1234...5678` 可被猜测 |
| 错误回显 | 绝对路径 + 完整 hash 直接返回前端 |

## 6. 风险定级

| 等级 | 项 | 理由 |
|---|---|---|
| P0 阻塞 | 路径逃逸、命令直透、`callId` 逃逸 | 可写任意文件/执行任意命令 |
| P0 阻塞 | 未知工具透传 | 模型可调用未授权工具 |
| P1 严重 | 备份非原子、无配额、脱敏弱 | 丢数据/打满磁盘/泄密钥 |
| P2 一般 | Schema 与实现不一致、双轨解析 | 误杀合法调用、难维护 |
