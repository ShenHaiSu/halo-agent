# need15-01 · P0 止血：5 个必修安全修复（先做）

> 目标：不依赖模型变聪明，也能防住**写坏文件 / 丢数据 / 批量污染**。
> 全部向后兼容（除注明两处），全部有单测 + 错误码 + 回滚路径。
> 预计：6 个源码文件 + 1 个测试文件 `tests/HaloAgent.Tests/Stage15P0SafetyTests.cs`。

---

## P0-1 · `regex` 下 `$` 被组展开导致写坏（最高优先级，纯 bug）

### 现状

`src/Host.Desktop/Tools/BuiltIn/EditFileTool.cs:270`：

```csharp
edits.Add((rawStart, rawEnd, m.Result(newString)));
```

`Match.Result` 会解释 `$& $1 ${name} $$`。模型写 C# 字符串插值 `$"{x}"`、bash `$PATH`、正则锚点 `$`、JS 模板 `${a}` 时，落盘内容被静默改写，且 diff 里不易察觉。

### 方案（选 A，必做）

**A. 默认字面写回（推荐，冻结语义）**：`regex` 的 `new_string` 默认按字面处理，不做组展开。如需捕获组，显式传新参 `regex_expand_groups:true` 才走 `m.Result`。

```csharp
// EditFileTool.cs — 参数新增
// "regex_expand_groups":{ "type":"boolean",
//   "description":"Only for match_mode=regex. If true, expand $1/${name} in new_string. Default false (literal write, safe for $ in code)." }

string replacement = regexExpandGroups ? m.Result(newString) : newString;
edits.Add((rawStart, rawEnd, replacement));
```

* 回吐追加一行标记（非错误，便于排查）：
  `LITERAL_DOLLAR_NOTE：new_string 含 $ 已按字面写回（如需 $1 展开请传 regex_expand_groups:true）。`
* Schema 的 `new_string` 描述追加：`For regex mode, $ is literal by default.`

**B（备选，不推荐）**：自动转义 `$`→`$$` 再 `m.Result`。缺点：双重转义心智重，不选。

### 验收

* `P0_1_RegexDollar_IsLiteral`：源含 `price = "$5"; var s = $"v={x}";`，regex 替换相邻行，断言 `$` 逐字节保留。
* `P0_1_RegexExpand_OptIn`：传 `regex_expand_groups:true` + `(a)(b)`→`$2$1`，断言展开仍可用。
* 旧测试 `Stage14 A7b_Regex…` 保持绿（其用例不含 `$`，不受影响）。

### 回滚

单 `if` 回退到 `m.Result` 即可；错误码无新增（`LITERAL_DOLLAR_NOTE` 只是回吐后缀，删掉不影响判读）。

---

## P0-2 · `write_file` 全量覆盖护栏 + 备份（防丢数据核心）

### 现状

`WriteFileTool.cs:78-120`：存在即继承编码换行后直接 `AtomicWriteAllText` 覆盖。无确认、无 size 上限、无 hash、无备份。模型拿截断的 `content`（长文件超上下文被截）调用一次即丢整文件。

### 方案

新增 3 个可选参数（缺省即保护，新建文件不受影响）：

```ts
write_file({
  file_path: string; content: string;
  allow_overwrite?: boolean;  // 默认 false：覆盖已存在文件必须显式 true
  expected_hash?: string;     // 可选：整文件字节 sha256（read_file/上次 write/edit 回吐里取）
  max_bytes?: number;         // 可选覆盖默认上限；默认 256_000（约 250KB）
})
```

执行顺序（严格按序）：

```
1. 解析参数；content 缺失仍按原逻辑拒绝。
2. if (!isNew) {
     if (contentBytes > effectiveMax) → Fail("CONTENT_TOO_LARGE：...请改用 edit_file/edit_lines 分片改。")
     if (!allow_overwrite) → Fail("NEED_CONFIRM_OVERWRITE：目标已存在（N 行/M 字节/hash:abc123）。确认全量覆盖请重调并传 allow_overwrite:true；局部改请用 edit_file/edit_lines。")
     if (expected_hash != null && mismatch) → Fail("EXPECTED_HASH_MISMATCH：...")
   }
3. 落盘前备份：copy 原文件 → <DataRoot>/backups/<ws>/<sess>/yyyyMMdd-HHmmss-<guid>-<basename>.bak
   （备份失败不阻塞？→ 阻塞：备份失败直接 Fail，防止无退路覆盖。）
4. AtomicWriteAllText；成功回吐追加 `hash:` + `backup:` 相对路径。
```

常量：`internal const int DefaultMaxWriteBytes = 256_000;` `internal const int HardMaxWriteBytes = 1_000_000;`（`max_bytes` 不得超过硬上限）。

备份目录复用 `ToolContext.DataRoot`（与 `ToolOutputGuard` 落盘同根），路径由 `StoragePathManager` 拼，单测可用临时目录注入。

### Schema/描述改法

* `Description` 改为：`Create a new file, or fully overwrite an existing file ONLY when allow_overwrite:true. For partial edits prefer edit_file/edit_lines. Overwrites are backed up.`
* `content` 描述追加 `For existing files >250KB use edit_* instead; truncated content will be rejected.`

### 验收

* `P0_2_Overwrite_RequiresFlag`：已存在文件不传 flag → `NEED_CONFIRM_OVERWRITE` 且字节不变；补 flag → 成功且 `.bak` 存在且内容等于旧字节。
* `P0_2_TooLarge_Rejected`：`content` 300KB 不传 `max_bytes` → `CONTENT_TOO_LARGE` 且文件不变。
* `P0_2_Hash_Guards`：传错 hash → `EXPECTED_HASH_MISMATCH`；传对 → 放行。
* 新建文件（`isNew`）无 flag 一次成功（兼容）。

### 回滚

删 3 个可选参数解析 + 恢复直接覆盖分支即可；备份目录残留文件无副作用。

---

## P0-3 · `edit_lines` 的 `limit` 缺省陷阱（静默切错行）

### 现状

`EditLinesTool.cs:108-111`：`if (!hasLimit) limit = 1;`。模型漏传/传错 `limit`（以为是“读几行”）即只换 1 行；`limit=0` 插入语义与 `read_file(limit)` 混淆。

### 方案（小 breaking，但救命）

1. **去掉隐式 `=1`**：`limit` 缺失 → `Fail("MISSING_LIMIT：缺少 limit。请先 read_file 确认行号，替换 N 行传 limit:N，在 offset 前插入传 limit:0。")`。
2. `limit=0` 保留但 Schema 改名提示：描述写 `limit: number of lines to REPLACE (0 = pure insert before offset). Required.`。
3. P2 前过渡：`AgentOrchestrator.HintForTool("edit_lines")` 追加 `Missing limit? ...`（P0-5 一起做）。

```csharp
if (!hasLimit)
  return Fail("MISSING_LIMIT：缺少必需参数 'limit'（替换 N 行传 N，纯插入传 0）。请先用 read_file 确认行号。");
if (limit < 0) return Fail(...); // 存量
```

`GetDefinition` 的 `required` 改为 `["file_path","offset","limit","new_text"]`（`new_text` 仍允许 `allow_empty` 逻辑）。

### 验收

* `P0_3_MissingLimit_Rejected`：不传 limit → `MISSING_LIMIT` 且文件不变。
* `P0_3_InsertZero_Ok`：`limit:0` 插入行为不变（存量 A6 用例补传 limit 后仍绿）。
* 存量 `Stage14 A6/A6b` 需同步补 `limit` 参数（测试代码同步改，行为断言不变）。

### 回滚

恢复 `if (!hasLimit) limit = 1;` 一行 + `required` 回退即可。

---

## P0-4 · 锚点拦截从“短锚点”扩展到“模糊多命中”

### 现状

仅 `replace_all + compact<8` 拦截（`EditFileTool.cs:202-217`）。`replace_all=false` 但命中 5 处通用锚点（如 `return true;`）时只报 `matched N times` 英文串，模型加两行上下文重试仍可能选中错误的那处（归一化后多处文本相同）。

### 方案

1. 保留 R5 短锚拦截，阈值与文案不变。
2. 新增 `ANCHOR_AMBIGUOUS`：`!replace_all && matchCount>1` 时，除报次数外，复用 `FindNearest` 给出 Top-3 行号 + 要求“加长到包含上下函数签名/花括号层级”：

```
ANCHOR_AMBIGUOUS：old_string 命中 5 处（归一化后相同）。已拒绝单处替换以防改错位置。
候选行：[12, 87, 203]（见 nearest）。
请加长锚点至包含外层函数名/类名后重试，或改用 edit_lines(offset,limit) 指定行。
```

3. `replace_all=true` 且 `matchCount>=20` 时追加警告行（不拒绝，但回吐首行 `WARN_BULK_EDITS：本次替换 N 处，请核对 diff`），20 为常量 `MaxBulkWarnThreshold`。

实现点：`BuildNotFoundDiagnostic` 已有 `nearest`，多命中分支复用它（传 `options` + `topN:3`），不新增引擎。

### 验收

* `P0_4_Ambiguous_Rejected_WithLines`：5 处相同 `return true;`，`replace_all:false` → `ANCHOR_AMBIGUOUS` 且含 `nearest` 行号，文件不变。
* `P0_4_ShortAnchor_StillRejected`：存量 A4 用例仍绿。
* `P0_4_BulkWarn`：构造 25 处命中 + `replace_all:true` + 长锚点 → 成功但输出含 `WARN_BULK_EDITS`。

---

## P0-5 · 结构化错误码：让失败可编程判读（P1/P2 的前提）

### 现状

`ToolResult { Success, Output, Error(string), ... }` 全靠字符串匹配；`AgentOrchestrator.HintForTool` 按工具名追加固定英文 hint，模型仍需读长串。

### 方案（最小侵入，不改序列化契约）

1. **错误码前置规范**：所有文件工具 `Fail` 的 `Error` 首 token 必须为 `CODE：`（全大写下划线 + 中文冒号/英文冒号均可，正则 `^([A-Z_]+)[:：]`）。P0 把存量码统一：
   * `OLD_STRING_NOT_FOUND`（已是 `{"ok":false,"code":"OLD_STRING_NOT_FOUND"…}`，保持，外层 Fail 前缀也加 `OLD_STRING_NOT_FOUND：` 以便不用解析 JSON 即可判读）。
   * 零长正则：`REGEX_ZERO_LENGTH：...`（原中文句，ows 前加码）。
   * 存量 `EMPTY_NEW_TEXT/EXPECTED_HASH_MISMATCH/OUT_OF_RANGE/NO_CHANGE/ANCHOR_TOO_SHORT` 保持，已合规。
2. `ToolResult` 新增可空字段（JSON 忽略 null，老消费方无感）：

```csharp
public string? ErrorCode { get; set; }      // 如 "OLD_STRING_NOT_FOUND"
public string? ErrorPayloadJson { get; set; } // edit_file 失配时的 nearest/hint JSON（即现 BuildNotFoundDiagnostic 产物）
```

`Fail(error, ...)` 不变；新增 `FailCode(code, message, payloadJson?, ...)` 工厂，`EditFileTool/EditLinesTool/WriteFileTool` 的 P0 新增分支必须走它。

3. `AgentOrchestrator.HintForTool` 按 `ErrorCode` 分流（先取 `ErrorCode`，取不到再正则首 token）：

```csharp
"OLD_STRING_NOT_FOUND" => " Hint: open read_file at the suggested line and copy-paste the anchor verbatim, or use edit_lines(offset,limit).",
"ANCHOR_AMBIGUOUS"     => " Hint: lengthen the anchor to include the enclosing function/class name, or use edit_lines.",
"NEED_CONFIRM_OVERWRITE"=> " Hint: retry write_file with allow_overwrite:true, or use edit_file/edit_lines for partial edits.",
"MISSING_LIMIT"         => " Hint: set limit = lines to replace (0 = insert).",
```

### 验收

* 所有 P0 新增/存量错误单测断言 `ErrorCode == "XXX"` 或 `Error.StartsWith("XXX")`。
* `HintForTool` 单测：每个码返回对应 hint 非空。
* 前端/网关序列化快照测试仍绿（新增字段 `WhenWritingNull` 不破坏）。

---

## 文件清单（P0 合计）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `Tools/BuiltIn/EditFileTool.cs` | P0-1 + P0-4 + P0-5（参数 `regex_expand_groups`，多命中分支，FailCode） |
| 2 | `Tools/BuiltIn/WriteFileTool.cs` | P0-2（3 参数 + 备份 + 上限） |
| 3 | `Tools/BuiltIn/EditLinesTool.cs` | P0-3（limit 必填）+ P0-5 |
| 4 | `Tools/ToolResult.cs` | P0-5（`ErrorCode/ErrorPayloadJson` + `FailCode`） |
| 5 | `Services/AgentOrchestrator.cs` | P0-5（`HintForTool` 按码分流） |
| 6 | `Tools/BuiltIn/FileToolShared.cs` | 新增 `ComputeBackupPath` helper（可选，或放 WriteFileTool 私有） |
| 7 | `tests/HaloAgent.Tests/Stage15P0SafetyTests.cs` | 新增 10 用例（上文各 2-3 个） |

性能约束：P0 不得让 1MB 文件 `normalized` 命中 >200ms（存量 Perf 用例仍绿）；备份为一次 `File.Copy`，不计入匹配预算。
