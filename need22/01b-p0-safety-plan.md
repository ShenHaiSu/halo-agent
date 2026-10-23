# need22-01b P0 安全施工方案

> 对应诊断：[01a](./01a-p0-safety-diagnosis.md)。每条独立提交、独立回滚。

## 1. 前置检查

- [ ] `dotnet test tests/HaloAgent.Tests -c Release` 全绿，记录基线（重点 `Stage15P0SafetyTests`、`Stage17*`）。
- [ ] 确认 `StoragePathManager.DataRoot`、`backups/`、`tool-outputs/` 实际路径，写进施工分支说明。
- [ ] 准备攻击样本库 `tests/HaloAgent.Tests/Stage15P0AttackSamples.cs`（5+3 条，见 §3 S1/S7）。

## 2. S1 路径越界改拒绝（含 symlink）

* **改**：`Tools/Safety/PathSafetyValidator.cs`
  1. `ValidateWrite/ValidateRead` 越界一律 `Allowed=false` + 错误码 `PATH_OUTSIDE_WORKSPACE`，删除“警告放行”分支。
  2. 新增 `NormalizeAndResolve(path)`：`GetFullPath` → `GetFinalPathNameByHandle`（解析 symlink/junction）→ 二次 `IsOutsideWorkspace` 校验。
  3. 增加 `AllowedRoots[]`（工作区 + `DataRoot/backups` 只读豁免），其余一律拒绝。
* **改**：`BuiltIn/WriteFileTool.cs:117-118,315`、`EditFileTool.cs`、`ReadFileTool.cs`、`ListDirTool.cs`、`GrepSearchTool.cs`、`FileToolShared.cs`：统一走新 `Validate*`，不得降级；`EditFile` 删除弃元 `_`。
* **单测**：`../`、`C:\Windows\...`、工作区内 symlink→区外，全部拒绝。

## 3. S2 命令执行收敛

* **改**：`BuiltIn/ExecCommandTool.cs` + `Tools/Process/CommandRunnerProbe.cs:51`
  1. 白名单：`git/node/pnpm/npm/python/pip/dotnet` 等（可配置 `AgentConfigService.AllowedCommands`），白名单外拒绝 `COMMAND_NOT_ALLOWED`。
  2. `command` 长度上限 8K，超限拒绝；`workdir` 强制 `ValidateWrite` 通过；`ComSpec` 固定 `%SystemRoot%\System32\cmd.exe`（不读环境变量）。
  3. 超时默认 60s、上限 300s；`timeout_ms<=0` 视为非法（不得无限等）。
  4. 命令全文记审计日志（`HaloLogger.Tool` 通道，参数脱敏后）。
* **单测**：越界 `workdir`、超长命令、`timeout=0`、白名单外命令，四组全拒。

## 4. S3 参数校验收敛

* **改**：`Tools/Validation/ToolCallValidator.cs:196`：未知工具返回 `UNKNOWN_TOOL`，删除透传分支。
* **改**：补 `edit_lines.limit` 校验（与业务层一致），业务层兜底删除。
* **改**：`SkillTool`/`GoalTool` 的 Schema 与实现对齐（二选一：补声明 `create/update/delete`，或拆分读写两个工具；推荐补声明 + 权限位）。
* **改**：各 `Handler.ExecuteAsync` 改用 `NormalizedJson`，删除重复 `JsonDocument.Parse`（逐个替换，每工具一提交）。
* **单测**：未知工具、`skill_tool create`、`goal_tool progress` 三组走通；`Stage17ValidationTests` 全绿。

## 5. S4 备份/撤销原子可靠

* **改**：`BuiltIn/BackupStore.cs`
  1. `WriteBackup` 改 tmp+Move+fsync（参考 `AtomicFileWriter`），失败不留半文件。
  2. `Prune` 按 `mtime` 删旧；文件名 `sessionId_callId_hash.json`（hash 定长 hex，不可再解析歧义）。
  3. 全局配额 500MB（可配置），超限拒绝并告警 `BACKUP_QUOTA_EXCEEDED`。
* **改**：`UndoEditTool.GuessOriginal` 删除，改为备份索引 `index.json` 精确回查。
* **单测**：并发 20 写不断电一致；配额压测拒绝；`Undo` 100% 找回。

## 6. S5 敏感信息封堵

* **改**：`ToolOutputGuard.SanitizeFileName`：`callId` 白名单 `[A-Za-z0-9_-]{1,64}`，非法直接拒绝落盘（`INVALID_CALL_ID`）。
* **改**：落盘前（spill/备份/memory）跑 `sk-/Bearer/aws_secret/...` 正则，命中则脱敏或拒绝并告警。
* **改**：`SecretProtector.MaskSecret` 全掩码（仅保留长度，如 `sk-****(len=51)`）。
* **改**：错误回显去绝对路径（相对工作区显示），hash 截断 12 位。
* **验证**：`grep -r "sk-" artifacts` 为空；日志抽查无绝对路径。

## 7. 提交切分（建议 8 个提交）

```text
s1-validator → s1-callers → s2-exec → s3-unknown → s3-schema → s3-normalized
  → s4-backup → s5-secrets
```

每个提交跑 `dotnet test Stage15* Stage17*`，绿才能进下一个。
