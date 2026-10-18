# need15-02 · P1 加固：让“正确调用”成为最顺手的调用

> 前置：P0 已合入（错误码冻结）。本期不改落盘语义，只改**提示词 + Schema + 回吐**，把一次成功率从 ~60% 拉到 ≥90%。
> 预计：4 个源码文件 + 1 个测试 `Stage15P1UsabilityTests.cs`。

---

## P1-1 · `ToolGuideTemplate` 从软建议升级为硬规则（性价比最高）

### 现状

`Core/Models/PromptModels.cs:49-54` 仅 4 行：`read first / prefer edit_file / use list_dir / relative path`。没有“禁止事项”，模型看完依然敢凭记忆编 `old_string`、敢用 `write_file` 改 3 行。

### 方案

替换为 8 行冻结英文（保持英文以利 prefix-cache 稳定，见 need09-02），追加到 `ToolGuideTemplate`，并同步进 `SkillMemoryGuideSuffix` 之后的三行指南之后（`SystemPromptBuilder.cs:130-133` 拼接处不变，只换模板文本）：

```
// 建议的新 ToolGuideTemplate（冻结，逐字写入 PromptModels.cs）
## 3. Standard Tool Usage Guidelines
- Before modifying ANY file, ALWAYS read the exact section with `read_file` first. NEVER invent `old_string` from memory.
- `old_string` MUST be a verbatim copy-paste from the latest `read_file` output (including indentation). Add 2-5 surrounding lines to make it unique.
- For partial edits: prefer `edit_file` (string anchor) for ≤50 lines; prefer `edit_lines(offset,limit)` for huge files, CRLF/Tab files, or after any `OLD_STRING_NOT_FOUND`.
- NEVER use `write_file` for partial edits. `write_file` overwrites the WHOLE file and requires `allow_overwrite:true`; truncated content will be rejected.
- `edit_lines` requires explicit `limit` (lines to replace; 0 = insert). Line numbers are 1-based, identical to `read_file`.
- If `edit_file` reports `OLD_STRING_NOT_FOUND` or `ANCHOR_AMBIGUOUS`, do NOT retry the same anchor. Follow the `hint` (use the suggested `edit_lines` line range).
- All relative paths resolve against `{{WorkspaceRoot}}`.
```

注意：`{{WorkspaceRoot}}` 变量保留，由 `RenderTemplate` 替换；其余为纯文本。`PromptDefaults` 变更会导致 `IsDefaultConfig` 快照变化——需同步更新 `Stage06PromptTests` 中逐字节断言的期望串（允许的一次性更新，在测试里注明 need15）。

### 验收

* `P1_1_Guide_ContainsHardRules`：`BuildAsync` 产物含 `NEVER invent` + `verbatim copy-paste` + `allow_overwrite:true` 三串。
* 抽样任务（见 04）：`OLD_STRING_NOT_FOUND` 后模型 1 步内转 `edit_lines` 的比例 ≥80%（人工抽 20 条）。

---

## P1-2 · Schema 收敛：把 4 个归一开关藏起来 + 给示例

### 现状

`EditFileTool.GetDefinition():36-47` 暴露 `match_mode/normalize_eol/expand_tabs/trim_trailing_space/ignore_leading_whitespace` 5 个旋钮。实测模型会传 `expand_tabs:0 + trim:false` 等自杀组合；`exact` 与 `normalized` 区别模型读不懂。

### 方案

1. **参数收敛（保持解析兼容，只改 Schema 呈现）**：
   * Schema 只保留 `match_mode: [normalized, exact, regex]` 三选一；删除 `normalize_eol/expand_tabs/trim_trailing_space/ignore_leading_whitespace` 的 Schema 声明（代码仍解析，老调用不断）。
   * `normalized` 服务端锁定最宽容组合：`{NormalizeEol:true, ExpandTabs:4, TrimTrailing:true, IgnoreLeading:false}`；`ignore_leading` 需要时由诊断 hint 建议（`ignore_leading_whitespace:true` 仍可传，只是 Schema 不展示——“隐藏高级项”）。
   * `new_string` 描述追加字面 `$` 说明（承接 P0-1）；`old_string` 描述追加 `min 16 chars, copy-paste from read_file` + `minLength:16` 约束（JSON Schema 加 `"minLength":16`，服务端同步校验：归一化后 needle 空或 <4 非空白字符即 `Fail("ANCHOR_TOO_SHORT…")`，与 R5 复用阈值思路但阈值更低，避免误伤单行改符号场景）。
2. **全工具补 `examples`**：在 `Description` 末尾追加一行极简示例（LLM 对示例的服从远高于文字规则）：

```
edit_file example: {"file_path":"src/a.ts","old_string":"  if (x) {\n    foo();\n  }","new_string":"  if (x) {\n    bar();\n  }"}
edit_lines example: {"file_path":"src/a.ts","offset":120,"limit":3,"new_text":"new line1\nnew line2\nnew line3"}
```

   实现方式：直接拼进 `Description` 字符串（不改 `ToolDefinition` 结构，避免双协议 ToChat/ToResponses 改动）。

3. `write_file` 的 Schema `content` 加 `"minLength":1` 提示 + 描述 `For existing files you must also pass allow_overwrite:true (see guide).`

### 验收

* `P1_2_Schema_HidesAdvancedKnobs`：`GetDefinitions()` 的 edit_file parameters 不含 `expand_tabs` 字符串，但传 `expand_tabs:0` 老参数仍被解析（兼容单测）。
* `P1_2_ShortAnchor_RejectedFast`：`old_string:"x"` → 首 token `ANCHOR_TOO_SHORT`（或新增 `ANCHOR_TOO_SHORT` 复用），文件不变。
* 双协议快照：`ToChatToolJson/ToResponsesToolJson` 仍为合法 JSON（`JsonDocument.Parse` 不抛）。

---

## P1-3 · 成功回吐补 `hash` + 行号：让下一次编辑可校验

### 现状

`edit_file` 成功回吐：`已修改 'x'（替换 N 处；Encoding…）+ diff`，无 hash、无行号。下一次 `edit_lines(expected_hash)` 无 hash 可用，形同虚设。

### 方案

1. `edit_file` 成功后计算 `newHash = HashText(updatedText, detected)`（复用 `EditLinesTool.HashText` 逻辑，抽到 `FileToolShared` 为 `ComputeTextHash(text, detected)` 公共方法），回吐追加：
   `, hash: <sha256>`，并在 `ToolResult` 回填 `FileBytes`（已在 write_file 有先例 `WriteFileTool.cs:136-138`，edit 侧补上）。
2. `read_file` 回吐首行追加 `Hash:`（整文件字节 sha256 前 16 位 + 全值？——定为全值 64 hex，模型复制方便；性能：`File.ReadAllBytes` 一次，5000 行内 <5ms，大文件可接受；超 1MB 文件只给前 16 位 + `hash_full:skipped(size)`，避免读两遍）。
   * 改动点：`ReadFileTool.cs:128-131` 的 header 拼装处。
3. `edit_lines` 成功回吐已含 `hash`（存量），追加 `range: line A..B → C 行` 已含（`301-305`），保持；补 `FileBytes` 回填（与 write/edit_file 对齐，供前端“完成态”展示）。

预算：仍受 `MaxSuccessOutputChars=2000` 约束，hash 行 (~80 字符) 计入预算内（`EditDiffBuilder` 的 `maxChars-320/-380` 预留已够，无需调参）。

### 验收

* `P1_3_EditFile_ReturnsHash`：成功输出正则 `hash: [0-9a-f]{64}`，且该 hash 与落盘字节 sha256 一致（单测重算比对）。
* `P1_3_ReadFile_ReturnsHash`：header 含 `Hash:`，且与 `edit_lines(expected_hash:<that>)` 联动一次通过（端到端 hash 链）。
* `P1_3_FileBytes_Filled`：edit_file/edit_lines 成功 `FileBytes > 0`。

---

## P1-4 · `read_file` 行号口径冻结声明（防 offset 漂移）

### 现状

`read_file` 用 `content.Split('\n')` 行号（`ReadFileTool.cs:117`），`edit_lines` 用 `SplitRawLines`（`EditLinesTool.cs:362`），两者在 `\r` 单独换行（老 Mac 风格）与空文件时差 1。模型在 CRLF 文件上 `offset` 漂移 1 行后整段错位。

### 方案（小改，冻结口径）

* 空文件：两者统一 `totalLines=1` 还是 `0`？——冻结为：空文件 `totalLines=0` 不合理（`Split` 得 `[""]`），保持现状 `read_file: [""]→1 行`，`edit_lines: SplitRawLines("")→1 行（Start:0,Len:0）`，一致即可。单测锁定：空文件 `offset=1,limit=0` 插入成功。
* `\r` 单独换行：`read_file` 目前 `Split('\n')` 后只去尾 `\r`，孤立 `\r` 不切行；`edit_lines` 的 `SplitRawLines` 把孤立 `\r` 当换行。统一为后者？——不改 `read_file`（影响面大），改为 `edit_lines` 容错：若 `offset` 按 `read_file` 口径越界但按 `SplitRawLines` 口径合法，错误信息同时给出两个口径的行数：
  `OUT_OF_RANGE：offset=…超出 read_file 口径总行数 X（edit_lines 物理行 Y）。请以 read_file 显示的行号为准。`
* 仅改错误文案 + 单测，不改切分算法（风险最小）。

---

## 文件清单（P1 合计）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `Core/Models/PromptModels.cs` | P1-1 模板文本替换 |
| 2 | `Tools/BuiltIn/EditFileTool.cs` | P1-2 Schema 收敛 + needle 最小长度校验 |
| 3 | `Tools/BuiltIn/ReadFileTool.cs` | P1-3 hash header |
| 4 | `Tools/BuiltIn/FileToolShared.cs` | P1-3 `ComputeTextHash` 公共化（从 EditLinesTool 搬） |
| 5 | `Tools/BuiltIn/EditLinesTool.cs` | P1-3 FileBytes + P1-4 越界文案 |
| 6 | `Tools/BuiltIn/WriteFileTool.cs` | P1-2 描述微调（1 行） |
| 7 | `tests/.../Stage15P1UsabilityTests.cs` | 新增 8 用例 |
| 8 | `tests/.../Stage06PromptTests.cs` | 期望串一次性更新（注明 need15） |
