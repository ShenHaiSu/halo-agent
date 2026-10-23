# need22-01 P0 后端安全加固

> 等级：P0，最高优。无前置依赖，可立即开工。

## 1. 前置检查

- [ ] `dotnet test tests/HaloAgent.Tests` 全绿，记录基线（重点 `Stage15P0SafetyTests`、`Stage17*`）。
- [ ] 确认工作区根 `StoragePathManager.DataRoot` 与 `backups/`、`tool-outputs/` 路径，记录在施工分支 README。
- [ ] 构造 5 条攻击向量样本：`../` 写逃逸、`callId=../../x`、`exec` 越界 `workdir`、`timeout_ms=0`、`symlink` 指向工作区外。

## 2. 既定目标

1. 路径越界从“警告放行”改为“默认拒绝”，读写统一规范化（含 symlink/junction 解析）。
2. `exec_command` 收敛：白名单 + 长度上限 + 工作区约束 + 显式超时 + `ComSpec` 固定。
3. 参数校验收敛：未知工具拒绝，`skill_tool`/`goal_tool` 声明与实现对齐，消除双轨手解析。
4. 备份/撤销原子可靠：原子写 + fsync + 全局配额 + 可恢复命名。
5. 敏感信息不落地明文：落盘目录逃逸封堵 + 密钥扫描 + 脱敏收紧。

## 3. 改动清单（对照代码现状）

| # | 文件 | 现状 | 改为 |
|---|---|---|---|
| S1 | `Tools/Safety/PathSafetyValidator.cs` + `BuiltIn/WriteFileTool.cs:117-118,315`、`EditFileTool.cs` | `OutsideWorkspace=true` 仍 `Allowed=true`，`EditFile` 直接弃元 `_` | 越界一律 `Allowed=false` 并返回 `PATH_OUTSIDE_WORKSPACE`；调用方不得降级为警告 |
| S2 | `BuiltIn/ReadFileTool.cs`、`ListDirTool.cs`、`GrepSearchTool.cs`、`Tools/FileToolShared.cs` | `ResolveReadPath/ResolveWorkspacePath` 允许绝对路径任意读，仅 `GetFullPath` | 统一走 `ValidateRead`；`GetFinalPathNameByHandle`/规范化解析 symlink 后二次校验 |
| S3 | `Tools/Process/CommandRunnerProbe.cs:51`、`BuiltIn/ExecCommandTool.cs` | `cmd.exe /d /s /c "command"` 直透，无黑白名单/长度上限，`workdir` 可逃逸，`ComSpec` 取环境变量，`timeout_ms=0` 无限等 | 白名单（`git/node/pnpm/python` 等）+ 命令长度上限（如 8K）+ `workdir` 强制工作区内 + `ComSpec` 固定 `System32\cmd.exe` + 超时默认 60s、上限 300s，`0` 视为非法 |
| S4 | `Tools/Validation/ToolCallValidator.cs:196` | 未知工具透传；`edit_lines` 缺 `limit` 放过业务层 | 未知工具拒绝 `UNKNOWN_TOOL`；补齐 `edit_lines.limit` 校验，业务层不再兜底 |
| S5 | `BuiltIn/SkillTool*.cs`、`GoalTool.cs`、`ToolSchemaBuilder.cs` | `skill_tool` Schema 仅 `list\|get` 却实现增删改，`goal_tool` 声明 `get\|set` 描述含更多动作 | Schema 与实现对齐；各 Handler 改用 `NormalizedJson`，删除重复手解析 |
| S6 | `BuiltIn/BackupStore.cs`、`UndoEditTool.cs`、`EditDiffBuilder.cs` | `File.Copy` 非原子无 fsync；`Prune` 按名字典序；`GuessOriginal` 按 `_` 切分 | tmp+Move+fsync；按 `mtime` 修剪；备份文件名 `sessionId/callId/hash.json` 不可解析歧义；全局配额（如 500MB）超限拒绝并告警 |
| S7 | `Tools/Safety/ToolOutputGuard.cs`（`SanitizeFileName`） | 仅过滤非法字符，`callId` 含 `../` 可逃逸 `tool-outputs/` | `callId` 白名单 `[A-Za-z0-9_-]{1,64}`，非法直接拒绝落盘 |
| S8 | `Infrastructure/Logging/HaloLogger.cs`、`SecretProtector.cs(MaskSecret)` | 明文 spill/备份/memory 无密钥扫描；`MaskSecret` 留首尾各 4 位；错误回显绝对路径+hash | 落盘前 `sk-/Bearer/secret` 正则扫描命中则脱敏或拒绝；`MaskSecret` 全掩码（保留长度）；错误信息去绝对路径，hash 截断 |

## 4. 验收标准与验收方法

| 验收项 | 标准 | 方法 |
|---|---|---|
| A1 路径逃逸 | 5 条攻击样本全部被拒绝，无文件写出 | 跑 P0 自带攻击样本脚本 + `Stage15P0SafetyTests` 新增用例绿 |
| A2 命令约束 | 越界 `workdir`/超长命令/`timeout=0` 全部拒绝；白名单外命令拒绝 | 单测 + 手工 `exec_command` 探针 |
| A3 参数收敛 | 未知工具/`skill_tool` 非法动作返回明确错误码，不再透传 | `ToolCallValidator` 单测 + 回归 `Stage17ValidationTests` |
| A4 备份可靠 | 断电/并发下备份可恢复；超配额拒绝；`Undo` 100% 找回 | 并发写脚本 + 配额压测 + `UndoEdit` 回放 |
| A5 无敏感落地 | 含 `sk-` 的输出落盘前被脱敏；日志无绝对路径 | 密钥样本灌入 + `grep -r "sk-" artifacts` 为空 |
| A6 无回归 | 全量 `dotnet test` 绿 | `dotnet test -c Release` |

## 5. 回滚

* 每条 S 独立提交；回滚单条即恢复旧校验函数，攻击样本库保留用于复验。
* S1/S3 回滚后必须重新跑 A1/A2 全量样本。
