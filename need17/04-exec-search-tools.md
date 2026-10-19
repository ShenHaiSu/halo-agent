# need17-04 · 执行搜索分册：exec_command / grep_search / list_dir / view_image

> 前置：01、02。对应 pi `toolDocs/05`（shell）、`08`（grep/find/ls）、`09`（截断/累积器）。
> 冻结原则：执行语义（JobObject 杀树、SmartDecoder、串行行为）不动；本分册只做**上限声明化 + 截断教学进描述 + Schema 闭合**。

---

## 4.1 `exec_command`（`Tools/BuiltIn/ExecCommandTool.cs:21-36`）

Schema（builder 生成）：

* `command` string 必填，描述：`Full command line (runner cmd) or Python code/script args (runner python). Runs in workspace root; stdout+stderr merged.`
* `runner` enum `[cmd,python]`，`default:cmd`，描述：`cmd first (Windows .bat/.cmd/.exe, dir/type/findstr/git/dotnet/node). python for text processing/calculation. No PowerShell.`（07 在无 Python 快照时动态追加不可用注记，见 07 §3）。
* `workdir` string 可选，描述：`Optional dir relative to workspace root. Defaults to workspace root. Missing dir fails fast.`。
* `timeout_ms` integer 可选，`minimum:0`（`0=infinite, cancellable`），描述：`Optional timeout in ms. 0 = infinite (user-cancellable). Defaults to server config (30000). Capped by server max (300000).`
* Description 首段保留 Win7 兼容句，追加：`Output keeps the TAIL (last lines) on truncation; full text beyond the budget is spilled to a log file.` + 示例：`{"command":"dir /b"} / {"command":"print('hi')","runner":"python"}`。
* 上限同源断言：Schema 文案的 `30000/300000` 与 `DefaultTimeoutMs/MaxTimeoutMs` 常量一致（单测锁定，改常量必须同步改文案）。

`runner` 非法值由 02 `ENUM_VIOLATION` 前置拒绝（替代现 `不支持的 runner` 手写分支，文案升级为码+可选表+收到的原文）。

### 验收

* `Exec_Schema_Closed` + `timeout:"30s"`→30000 宽容（02 shim）+ 非法 runner 码断言；老 `Stage05ToolExecutionTests` 中 exec 用例全绿。

## 4.2 `grep_search`（`Tools/BuiltIn/GrepSearchTool.cs:27-36`）

* 主名 `file_path`？——否。grep 的是“在哪搜”，冻结为 `path` **改名** `dir_or_file` 太折腾；决策：主名保持 `path`（存量调用最多），**反向**兼容 `file_path`→`path` 搬运（02 shim 加一条 grep/list 特例，warning 同样 `ALIASED_PARAM`）。01 §1.4 的“全工具 file_path”修正为：**文件指向型工具**（read/write/edit*/view）用 `file_path`；**目录/搜索范围型**（grep/list）用 `path`。描述中写死，避免模型混淆。
* `pattern` string 必填，描述：`Regular expression to search. Returns relative_path:line_number:matched_line, up to 100 matches. Empty result is normal (no match), not an error.`
* `path` string 可选 default workspace root；`include_glob` string 可选（如 `*.cs;*.ts`）；描述补：`Directories .git/node_modules/bin/obj/dist are skipped. Long lines cut at 300 chars; use read_file for the full line.`
* 上限声明：`MaxMatches=100/MaxLineDisplayChars=300` 进描述（与常量同源断言）。

### 验收

* `Grep_Schema_Closed`：required=[pattern]；`file_path` 别名搬运成功；超长行截断提示含 `read_file`。

## 4.3 `list_dir`（`Tools/BuiltIn/ListDirTool.cs:27-32`）

* 现状无 `required` 是 bug：补 `required:[]` 显式空（Schema 合法）+ `additionalProperties:false`；`path` 可选（default `.`）；`recursive` boolean default false；`max_depth` integer `minimum:1 maximum:10 default:2`（与 `DefaultMaxDepth/MaxAllowedDepth` 同源）。
* 描述：`List directory with sizes. Noisy dirs auto-excluded. Directories end with /. Empty dir returns an empty marker, not an error.`
* PromptBrief：`List directory contents`；Coding preset 含 list_dir？——否（Coding 保持 pi 式 5 件，list 由 exec `dir` 兜底 + 06 条件规则；ReadOnly/Full 含 list_dir）。

### 验收

* `ListDir_Schema_Closed`：无 required 但 Schema 合法闭合；`max_depth:99`→`CONSTRAINT_VIOLATION`（maximum 10）。

## 4.4 `view_image`（`Tools/BuiltIn/ViewImageTool.cs`）

* 主名统一为 `file_path`（文件指向型），`path` 别名搬运；required=[file_path]；描述补：`Workspace image only. Non-image files fail fast. On non-vision models the image is stripped and text continues (see notice).`
* 与 06 联动：非视觉模型剥离逻辑（`AgentOrchestrator` 现有）保持，描述中一句话预告即可，不新增语义。

### 验收

* `ViewImage_Schema_Closed` + 别名搬运；双协议解析通过。

## 4.5 截断教学统一行（四个工具描述共用，冻结英文）

> `Large outputs are truncated to head+tail within a fixed budget; the full text is spilled to a log file path in the result. Use read_file(offset,limit) or grep_search to page, never re-run a broad command to dump everything.`

* 落点：exec/grep/list 的 Description 尾 + read（03 已写）形成闭环；`ToolOutputGuard` 的 16K/4K/8K 数字**不进 Schema**（执行层细节），描述只讲行为（head+tail+落盘+翻页），避免数字漂移（上限数字只在各工具自己的行数/条数字段声明）。
