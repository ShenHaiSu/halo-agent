# need17-03 · 文件工具分册：read / write / edit_file / edit_lines / undo

> 前置：01（builder+preset）、02（Validator+码表）。对应 pi `toolDocs/04`（read）、`06`（edit）、`07`（write）、`09`（截断/串行）。
> 冻结原则：只改**声明（Schema+描述）与参数校验前置**，不改落盘算法（匹配引擎、换行/BOM、串行队列、备份、diff 预算全部保持 need14/15 现状）。

---

## 3.0 分工冻结（写入 06 ToolGuide 的同一句话来源）

* 新文件、小文件全量重写 → `write_file`（覆盖需 `allow_overwrite:true`）。
* 存量文件局部改、锚点清晰 → `edit_file`。
* 大文件/CRLF-Tab 重灾区、锚点失配后 → `edit_lines(offset,limit)`。
* 误改回滚 → `undo_edit`。`edit` 分发器隐藏（01 已定），提示词不再出现。

## 3.1 `read_file`（`Tools/BuiltIn/ReadFileTool.cs:13-27`）

Schema（builder 生成，`additionalProperties:false`）：

* `file_path` string 必填，描述：`Relative path from workspace root or absolute file path. Use list_dir first if unsure.`（兼容别名 `path` 由 02 shim 处理，Schema 不写）。
* `offset` integer 可选，`minimum:1`，`default:1`，描述：`1-based first line. Out-of-range returns an error with total lines.`。
* `limit` integer 可选，`minimum:1`，`maximum:2000`，`default:500`，描述：`Max lines to return. Capped at 2000; more lines need next offset. Text beyond the tool output budget is truncated and spilled to a file (see guide).`
* Description 首段追加：`Returns 1-based line numbers. Large outputs are truncated (head+tail) with the full text spilled to a log file you can page with offset/limit.`

执行层不动（`DefaultLimit=500/MaxLimit=2000` 常量即上限来源，与 Schema `maximum:2000` 同源断言）。

### 验收

* `Read_Schema_DeclaresPaging`：required=[file_path]，limit maximum=2000 与常量一致；`offset:"10"` 经 02 宽容为 10。

## 3.2 `write_file`（`Tools/BuiltIn/WriteFileTool.cs:23-36`）

* `file_path` string 必填；`content` string 必填 `minLength:1`，描述追加：`Full file content (not a patch). Existing files >250KB must use edit_* instead; truncated content is rejected. Overwriting an existing file requires allow_overwrite:true.`。
* `allow_overwrite` boolean，`default:false`，描述：`Required true to overwrite an existing file.`。
* `expected_hash`/`max_bytes`/`dry_run` 保留可选，描述各补一句上限语义（`max_bytes` maximum=1000000 与 `HardMaxWriteBytes` 同源断言）。
* Description 首行保持（need15 已定），追加：`Example (new file): {"file_path":"src/new.ts","content":"export const x = 1;\n"}`。
* `content` 非 string 一律 `TYPE_MISMATCH`（02 shim 表已定：不猜，避免整文件写坏）。

### 验收

* `Write_Schema_Closed` + 老 `Stage15P0SafetyTests` 全绿（覆盖确认/上限/hash 语义零变化）。

## 3.3 `edit_file`（`Tools/BuiltIn/EditFileTool.cs:37-54`）

* `file_path` string 必填；`old_string` string 必填 `minLength:16`，描述：`Verbatim copy-paste from read_file (2-5 context lines for uniqueness). Min 16 chars.`；`new_string` string 必填，描述：`Replacement text. $ is literal unless regex_expand_groups:true.`。
* `replace_all` boolean default false；`match_mode` enum `[normalized,exact,regex]` default normalized；`regex_expand_groups/expected_hash/dry_run` 可选保留。
* **隐藏高级旋钮**（need15 P1-2 已要求，need17 落实到 builder）：Schema 不再展示 `normalize_eol/expand_tabs/trim_trailing_space/ignore_leading_whitespace`（服务端仍解析，老调用不断）。
* Description 追加分工句：`For single-file small edits (multi-anchor in one call is not supported; use multiple calls or edit_lines). Large refactors use write_file.` + 示例行（need15 文案保留）。

### 验收

* `EditFile_Schema_HidesAdvancedKnobs`（need15 P1-2 用例迁移不断）；短锚 `ANCHOR_TOO_SHORT` 路径经 02 前置（minLength）+ 业务层双保险。

## 3.4 `edit_lines` + `undo_edit`

* `edit_lines`（`EditLinesTool.cs:28-37`）：required `[file_path,offset,limit,new_text]` 不变（need15 P0-3 冻结）；各描述补 `default`/语义：`offset` minimum 1（`1-based, identical to read_file`）；`limit` minimum 0（`lines to REPLACE; 0 = insert before offset`）；`new_text`（`LF newlines converted to file EOL; empty needs allow_empty:true`）；`allow_empty/expected_hash/dry_run` 可选保留。`limit` 缺失继续 `MISSING_LIMIT`（02 码表已定，业务层文案不动）。
* `undo_edit`：Schema 不动（已闭合则只补 `additionalProperties:false`）；描述补一句：`Restores the most recent backup of file_path made by edit_*/write_file. Use after a bad edit instead of hand-reverting.`
* `PromptBrief/规则`（供 06 装配）：read=`Read file content with 1-based paging` + 规则`Use read_file to view files, not cat/type via exec_command.`；edit_file=`Anchor-based partial edit` + 规则（old 必须原文复制/多处分多次/相邻合并）；edit_lines=`Line-range edit without matching`；write=`Create or fully overwrite a file` + 规则`write_file for new/full rewrite only.`。

### 验收

* `Stage17CoreToolsTests` ≥10 用例：Schema 快照 + 别名搬运 + limit 缺失码 + undo 闭合 + 双协议解析通过。
