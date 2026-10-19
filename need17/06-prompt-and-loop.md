# need17-06 · 提示词重写 + 循环加固（hooks 预留）

> 前置：01（PromptBrief/Rules 元数据）、02（warnings+新码）、03/04/05（各工具分工与上限文案）。
> 对应 pi `toolDocs/10`（提示词装配）+ `02`（循环/截断保护/钩子）+ `01 §1.3/1.5`（前后置钩子/包装）。
> 冻结原则：提示词只教**选型与红线**，不教调用语法（语法靠 Schema，呼应 pi 10.1）；循环保持串行，不引入真并行。

---

## 6.1 `ToolGuideTemplate` 重写（冻结英文，逐字写入 `PromptModels.cs`）

```text
## 3. Standard Tool Usage Guidelines
- Before modifying ANY file, ALWAYS read the exact section with `read_file` first. NEVER invent `old_string` from memory.
- `old_string` MUST be a verbatim copy-paste from the latest `read_file` output (including indentation). Add 2-5 surrounding lines to make it unique.
- Small partial edit with a clear anchor: `edit_file`. Huge/CRLF/Tab files, or after any `OLD_STRING_NOT_FOUND`/`ANCHOR_AMBIGUOUS`: `edit_lines(offset,limit)`.
- NEVER use `write_file` for partial edits. `write_file` creates new files or fully overwrites (requires `allow_overwrite:true`); truncated content is rejected.
- `edit_lines` requires explicit `limit` (lines to REPLACE; 0 = insert before offset). Line numbers are 1-based, identical to `read_file`.
- If `edit_file` reports `OLD_STRING_NOT_FOUND` or `ANCHOR_AMBIGUOUS`, do NOT retry the same anchor. Follow the `hint` (use the suggested `edit_lines` line range).
- Locate first: `list_dir` for structure, `grep_search` for content. Do NOT `cat/type` files via `exec_command` to view them.
- `exec_command` runs in the workspace root (stdout+stderr merged, tail kept). Prefer `cmd`; `python` only for text processing/calculation. Never PowerShell unless asked.
- Large outputs are truncated (head+tail) with full text spilled to a log file path in the result. Page with `read_file(offset,limit)` or narrow `grep_search`; never re-run a broad dump.
- All relative paths resolve against `{{WorkspaceRoot}}`.
- For risky/large edits, call with `dry_run:true` first to preview the diff, then repeat without it.
```

* `{{WorkspaceRoot}}` 保留变量；其余纯文本。`Stage06PromptTests` 期望串一次性更新（注明 need17）。
* CmdPython 无 Python 时的条件 warning 行由 07 追加（06 预留拼接点，不实现）。

### 验收

* `Guide_Contains_HardRules`：产物含 `NEVER invent` + `verbatim copy-paste` + `allow_overwrite:true` + `dry_run:true` + `never re-run a broad dump`。

## 6.2 工具段 + 规则段动态装配（替代静态 8 行）

现状 `SystemPromptBuilder.BuildCore` 的 toolGuide 是整块模板。方案：

1. `ToolGuideTemplate` 保留上文通用纪律（静态）。
2. 新增动态两段（`BuildCore` 内按**激活集**拼装，顺序冻结：通用纪律 → 工具段 → 规则段）：
   * 工具段：`## Available Tools` + 逐行 `- name: brief`（取 01 `PromptBrief`；无 brief 的工具省略；空集写 `none`）。
   * 规则段：激活工具的 `PromptRules` 去重拼接（隐藏声明的工具规则同步剔除，呼应 pi 10.7 易漏点）。
3. 条件规则（抄 pi 10.3 §38）：激活集中有 exec 但无 grep/list 任一时，自动加一行 `Use exec_command (dir/findstr) for file listing and searching.`。

### 验收

* `DynamicToolSection_ReflectsPreset`：Coding 产物工具段恰 5 行；Full 产物含 overlay 5 行；规则去重无空行。

## 6.3 `HintForTool` 补新码（02 占位的落子）

| Code | Hint（一句英文，拼在 tool 消息尾） |
|---|---|
| `MISSING_REQUIRED_PARAM` | `Hint: re-issue the same call with the missing field added; do not change other fields.` |
| `TYPE_MISMATCH` / `ENUM_VIOLATION` / `CONSTRAINT_VIOLATION` / `UNKNOWN_ARGUMENT` | `Hint: fix the named field per schema (type/enum/limit) and retry; see the received-args echo.` |
| `TOOL_NOT_ENABLED` | `Hint: the tool is not in this turn's preset; use an active tool or ask for Full preset.` |
| `INVALID_JSON_ARGUMENTS` | `Hint: resend with complete JSON (no truncation); keep strings escaped.` |
| `PYTHON_NOT_AVAILABLE` | 见 07（中文引导，唯一中文 Hint） |

* `BuildNextActionHint` 对 `MISSING_REQUIRED_PARAM` 追加最小补全草稿（`{缺失字段: <fill>}` ≤300 字符，纯文本不执行）。

## 6.4 Agent 循环：截断回复保护（只加保护，不加并行）

现状：`ChatCompletionsAdapter.ParseStreamAsync` 忽略 `finish_reason=length`，半截 args 照样执行。

方案（最小改）：

1. Adapter 解析 `finish_reason`：`length`→ 新增 `UnifiedStreamEventKind.StreamTruncated` 事件（含已收 toolCall 数量）；`tool_calls` 保持现状。
2. `AgentOrchestrator` 收齐事件后：若 `StreamTruncated` 且本轮有 toolCalls → **跳过全部 `Dispatch`**，逐个落 `Tool` 消息：`[Tool Discarded - name]: 调用未执行：回复因输出长度被截断，参数可能不完整。请用完整参数重发（可缩小单次改动）。`（中文，呼应 pi 02 §2.3 文案大意），且该轮不计 `toolExecutions`。
3. 并行：本期**不做**（串行+文件串行队列已等价正确）；`ToolDefinition` 加 `ConcurrencyHint: Sequential|Parallel` 字段占位 + 注释，供未来文档扩并行（06 只加字段，不加调度）。

### 验收

* `TruncatedReply_Discards_ToolCalls`：mock 流 `finish_reason=length` + 半截 args → 零 `Dispatch` 调用，tool 消息含“调用未执行”；`Stage06AgentOrchestratorTests` 全绿。

## 6.5 hooks 插槽（只定接口，不接实现）

对应 pi 前/后置钩子（权限/脱敏/审计）。need17 只加接口 + 默认透传实现，零行为变化：

```csharp
public interface IToolHook { // Tools/Hooks/
  Task<ToolHookDecision> BeforeAsync(ToolCallDto call, string normalizedArgsJson, ToolContext ctx, CancellationToken ct);
  Task<ToolResult> AfterAsync(ToolCallDto call, ToolResult result, ToolContext ctx, CancellationToken ct);
}
public sealed class PassThroughToolHook : IToolHook // 默认注册
```

* `DispatchAsync` 在 Validator 通过后调 `BeforeAsync`（返回 Blocked→`FailCode("TOOL_BLOCKED", reason)` 不执行），执行后调 `AfterAsync`（整字段覆盖语义，抄 pi：给了 content 没给 structured 则丢 structured——本期无 structured，直接覆盖 `Output/Error/Success` 三字段）。
* 前端审计/密钥脱敏留给 need18，本期单测只断言透传。

### 验收

* `Hook_Passthrough_NoBehaviorChange`：默认 hook 下老用例全绿；Blocked 路径单测 1 例。
