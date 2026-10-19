# need15-03 · P2 架构：预览 + 备份撤销 + 统一入口 + 自愈闭环

> 前置：P0（安全）+ P1（易用）已合入。本期做“不敢做的大改也敢做”：先看 diff 再落盘，误删一步恢复，失败自动给下一步。
> 预计：7 个源码文件 + 1 个测试 `Stage15P2ArchitectureTests.cs`。全部可选参数，默认关闭，不影响存量。

---

## P2-1 · `dry_run`：先看 diff 再落盘（所有写工具通用）

### 设计

给 `edit_file / edit_lines / write_file` 各加一个可选布尔：

```ts
dry_run?: boolean; // 默认 false。为 true 时只计算、不写盘，回吐同样格式的 diff + hash_preview
```

语义冻结：

* `edit_file(dry_run:true)`：走完匹配 + `ToRawSpan` + 内存 `StringBuilder` 拼出 `updatedText`，**跳过 `AtomicWriteAllText`**，回吐首行 `DRY_RUN：未写盘（命中 N 处），下面是预览 diff：` + 同 R4 的 `EditDiffBuilder.Build` diff（同样 ≤2000 字符预算）。
* `edit_lines(dry_run:true)`：同理，跳过写盘，回吐 `DRY_RUN：line A..B → C 行（未写盘）` + diff。
* `write_file(dry_run:true)`：不建父目录不写盘，回吐 `DRY_RUN：X 行/Y 字节（未写盘），hash_preview: …`。超 `CONTENT_TOO_LARGE` 仍先报超限（ dry_run 也校验，防止预览通过、实调被拒）。
* `dry_run` 与 `replace_all/allow_overwrite/expected_hash` 正交：校验照常跑（hash 不符 dry_run 也报 `EXPECTED_HASH_MISMATCH`），只是最后一步不落盘。备份在 dry_run 下不产生。

实现技巧：三个工具把“计算 updatedText”抽为私有 `ComputeUpdated(...)`，`dry_run` 分支在 `AtomicWriteAllText` 前 `return Ok(preview)`。约各 15 行。

### ToolGuide 追加一行（P1 模板上再加，不单独立项）

```
- For risky/large edits, call with `dry_run:true` first to preview the diff, then repeat without it.
```

### 验收

* `P2_1_DryRun_NoWrite`：三工具 dry_run 后文件 `mtime+bytes` 不变，但输出含 `DRY_RUN` + `@@` diff。
* `P2_1_DryRun_Validates`：hash 错 + dry_run → 仍 `EXPECTED_HASH_MISMATCH`（证明不是跳过校验）。

---

## P2-2 · 备份保留 + `undo`（误删一步恢复）

### 设计

P0-2 已做单次 `.bak`。P2 把它产品化：

1. **备份命名冻结**：`<DataRoot>/backups/<workspaceId>/<sessionId>/yyyyMMdd-HHmmss-fff_<guid8>_<basename>.bak` + 同名 `.meta.json`（含 `src:relativePath, bytes, sha256, tool:edit_file/write_file/edit_lines, at:iso8601`）。
   * 保留策略：每会话最多 50 个，超限按时间删最旧（`BackupStore.Prune(sessionDir, keep:50)`，10 行）。
   * `edit_file/edit_lines` 的每次成功覆盖也写备份（P0 只有 write 有，本期补齐——复用同一 `BackupStore.WriteBackup(fullPath, workspaceId, sessionId)`，返回 `(backupAbsPath, hash)`）。
2. **新增工具 `undo_edit`**（可选，推荐做；如排期紧可降级为“回吐 backup 路径 + 文档说明手动恢复”）：

```ts
undo_edit({ backup_path: string; restore_to?: string })
```

   * `backup_path` 必须位于 `<DataRoot>/backups/` 下（`PathSafetyValidator` 新增 `ValidateBackupRead`，防任意读）。
   * 落盘同样走 `AtomicWriteAllText` + 再备一次当前（形成“撤销的撤销”链）。
   * 成功回吐 `已从 backup 恢复 'x'（N 字节，hash:…）`。

`BackupStore` 放 `Tools/BuiltIn/BackupStore.cs` 新文件（~120 行），三工具只调它，不各自拼路径。

### 验收

* `P2_2_BackupChain`：write→edit→edit 三次，backups 目录 3 个 `.bak` + 3 个 `.meta.json`；`undo_edit(latest)` 后字节等于第二次后。
* `P2_2_Prune`：造 55 个备份，触发一次写后剩 50 个。
* `P2_2_PathEscape_Rejected`：`backup_path:"C:/Windows/…"` → 拒绝。

---

## P2-3 · 自愈闭环：失败负载直接给出“下一步调用”

### 现状

P0-5 后错误有码，P1 后 hint 有行号，但仍需模型自己拼下一次参数。长上下文下模型常拼错 `offset/limit`。

### 设计（网关侧 30 行，效果最大）

在 `AgentOrchestrator` 的 tool 失败回包处（`ToolCallDto→toolMessage` 拼装 `ToolCallDto.cs:696-702`），对三类码自动追加**可直接复制的 JSON 草稿**（不是自动执行，只是给现成参数）：

```csharp
// 伪代码（AgentOrchestrator.cs ~700 行处）
if (!toolResult.Success && toolResult.ErrorCode is "OLD_STRING_NOT_FOUND" or "ANCHOR_AMBIGUOUS")
{
    // ErrorPayloadJson 里已有 nearest[0].line + old_string_lines
    toolText += $"\nNextAction (copy-paste and adjust): edit_lines({{\"file_path\":\"{path}\",\"offset\":{bestLine},\"limit\":{needleLines},\"new_text\":\"<paste full replacement here>\"}})";
}
if (code is "NEED_CONFIRM_OVERWRITE")
    toolText += "\nNextAction: retry the SAME write_file call with \"allow_overwrite\":true added.";
if (code is "MISSING_LIMIT")
    toolText += "\nNextAction: retry edit_lines with explicit \"limit\" (replace N lines, or 0 to insert).";
```

* 草稿中 `new_text` 留占位，不替模型编内容（防幻觉写盘）。
* 前端无需改动（纯文本追加，受 `ToolOutputGuard` 预算保护；追加控制在 300 字符内）。

备选（更强，需前端配合，列为可选项）：网关直接返回结构化 `next_action:{tool,args_draft}` 字段，前端渲染“一键重试”按钮。本期不做，留 `TODO(need16)`。

### 验收

* `P2_3_NextAction_Emitted`（编排层单测，用 fake ToolResult）：三类码回包含 `NextAction` 且为合法 JSON 草稿（`JsonDocument.Parse` 草稿部分不抛——占位符替换后）。
* 人工抽样（见 04）：模型按草稿重试的格式合法率 100%（20 条）。

---

## P2-4 · 统一 `edit` 入口（可选项，排期紧可砍）

### 取舍声明

P2-4 是唯一“加法架构”，收益是减少选型错误（模型不再纠结 edit_file vs edit_lines），代价是新增第 12 个工具。若排期紧，**允许砍**，P2-1~P2-3 即可结项。

### 设计（若做）

新增 `edit` 工具，参数：

```ts
edit({
  file_path: string;
  mode: "text" | "lines";       // text=走 EditFileTool 引擎，lines=走 EditLinesTool 引擎
  old_string?: string; new_string?: string; replace_all?: boolean; match_mode?: string;
  offset?: number; limit?: number; new_text?: string;
  dry_run?: boolean; expected_hash?: string;
})
```

* 实现为门面：按 `mode` 分发到 `EditFileTool`/`EditLinesTool` 的内部 `ComputeUpdated`，不复制匹配逻辑。
* `GetDefinition` 用 `anyOf` 约束两组必填（二选一），Schema 较长，需单测覆盖双协议序列化。
* 老 `edit_file/edit_lines` 保留（不删除），`edit` 的 Description 写 `Unified entry; prefer this over edit_file/edit_lines for new code.` 逐步引流。

---

## 文件清单（P2 合计）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `Tools/BuiltIn/EditFileTool.cs` | dry_run 分支 + ComputeUpdated 抽取 |
| 2 | `Tools/BuiltIn/EditLinesTool.cs` | 同上 |
| 3 | `Tools/BuiltIn/WriteFileTool.cs` | dry_run + 备份改调 BackupStore |
| 4 | `Tools/BuiltIn/BackupStore.cs` | 新增（命名/prune/meta/恢复） |
| 5 | `Tools/BuiltIn/UndoEditTool.cs` | 新增（可选）+ `ToolRegistry` 注册 |
| 6 | `Services/AgentOrchestrator.cs` | P2-3 NextAction 追加 |
| 7 | `Tools/Safety/PathSafetyValidator.cs` | 新增 `ValidateBackupRead` |
| 8 | `tests/.../Stage15P2ArchitectureTests.cs` | 新增 8 用例 |
