# need14 实施与验收报告：edit_file 工具改进

> 对应需求稿：[`TOOL_IMPROVEMENT_EDIT_FILE.md`](./TOOL_IMPROVEMENT_EDIT_FILE.md)
> 状态：**已完成**（R1–R5 全部落地，A1–A7 全绿）。改动只在网关工具层 + 测试 + 文档，不写业务源码。

## 1. 落地清单（代码级）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/Host.Desktop/Tools/BuiltIn/EditMatchEngine.cs` | 新增 | 宽容匹配引擎：换行 / Tab / 行尾空格 / 行首缩进归一化 + 「归一化索引 → 原始字节区间」双向映射 + Top-N 相似行诊断（Bigram Dice） |
| `src/Host.Desktop/Tools/BuiltIn/EditDiffBuilder.cs` | 新增 | R4 Unified Diff 生成器：±5 行上下文 + 硬字符预算，超限即截断并置 `truncated` |
| `src/Host.Desktop/Tools/BuiltIn/EditFileTool.cs` | 重写 | R1 三模式匹配、R3 结构化诊断、R4 回吐 ≤2000 字符、R5 短锚点拦截 |
| `src/Host.Desktop/Tools/BuiltIn/EditLinesTool.cs` | 新增 | R2 `edit_lines`：按 1-based 行号整段替换，零字符串匹配；`expected_hash` / `allow_empty` |
| `src/Host.Desktop/Tools/ToolRegistry.cs` | 改 | 注册 `edit_lines`（内置工具 10 → 11） |
| `src/Host.Desktop/Services/AgentOrchestrator.cs` | 改 | `edit_lines` 纳入流式字节计数；`HintForTool` 增加 `edit_lines`，`edit_file` 提示改为指向诊断载荷 |
| `tests/HaloAgent.Tests/Stage14EditFileToolTests.cs` | 新增 | A1–A7 + 性能预算，共 10 个用例 |
| `tests/HaloAgent.Tests/Stage05ToolExecutionTests.cs` | 改 | 内置工具数量断言 10 → 11，并纳入 `edit_lines` |

## 2. R1：宽容匹配（核心）

新增参数（**全部可选**，老调用零改动）：

```ts
edit_file({
  file_path, old_string, new_string, replace_all,   // 旧 4 个参数语义不变
  match_mode?: 'exact' | 'normalized' | 'regex',    // 默认 'normalized'
  normalize_eol?: boolean,                          // 默认 true：CRLF / CR → LF
  expand_tabs?: number,                             // 默认 4：Tab 按 N 空格展开（仅比对）
  trim_trailing_space?: boolean,                    // 默认 true：忽略行尾空白差异
  ignore_leading_whitespace?: boolean,              // 默认 false：忽略行首缩进差异
})
```

| 模式 | 语义 | 适用 |
|---|---|---|
| `normalized`（默认） | EOL + Tab + 行尾空格归一化后子串匹配 | 解决 Tab/空格、CRLF/LF、行尾空格造成的 `old_string not found` |
| `exact` | 仅按 `normalize_eol` 归一，其余一律不容忍（与历史行为等价） | 需要严格一致时显式指定 |
| `regex` | `old_string` 当作 .NET 正则（`Multiline`），`new_string` 支持 `$1` 捕获组 | 批量 / 模式化改写（常配 `replace_all=true`） |

实现要点：

- **只换目标区**：归一化时维护 `RawStart[i] / RawEnd[i]`（归一化字符 → 原始字符区间），命中后直接对原始文本 splice。文件其余字节（含混合换行、Tab 风格）零改动；`new_string` 的换行按「命中区间实际换行风格」转换（单行命中回退文件主换行）。
- **行尾扩展**：每行最后一个可见字符的 `RawEnd` 向右延伸到行尾，命中行尾时残留的行尾空格一并被替换（不产生 `value,   ` 尾渣）。
- **行首对齐**：命中若从某行首字符开始，替换区间回退到该行行首（`ignore_leading_whitespace` 下不会留半截缩进）。
- `normalize_eol=false` 严格保留 `\r\n`；`expand_tabs=0` 不展开 Tab。

## 3. R2：`edit_lines`（逃生通道）

```ts
edit_lines({
  file_path: string;
  offset: number;         // 1-based，与 read_file 行号口径完全一致
  limit: number;          // 覆盖行数；0 = 在 offset 前纯插入
  new_text: string;       // LF 即可，落盘按目标区间原换行风格转换
  expected_hash?: string; // 可选 sha256（整文件字节 或 覆盖行文本），不符即拒绝
  allow_empty?: boolean;  // new_text 为空（整段删除）必须显式 true
})
```

语义与保护：

- 行切分支持 CRLF / LF / 混合换行，行数与 `read_file`（`content.Split('\n')`）严格一致；成功回吐新文件 `hash`，可直接用于下一次 `expected_hash`。
- 换行继承：优先用被替换区间最后一行的换行符，其次用目标行换行符，最后回退文件主换行 —— 保证「只改目标行、换行风格不变」。
- 错误码：`EMPTY_NEW_TEXT`（空文本未授权）、`EXPECTED_HASH_MISMATCH`（并发改动）、`OUT_OF_RANGE`（offset 越界）、`NO_CHANGE`（无实质变化）。
- 同样受 R4 回吐上限（≤2000 字符）约束。

## 4. R3：失败诊断（省 token 的关键）

`old_string` 未命中时返回**结构化 JSON**（承载在 `ToolResult.Error`，直接进模型上下文），不再是一句话：

```json
{"ok":false,"code":"OLD_STRING_NOT_FOUND","file":"src/bridge/hostBridge.ts","file_eol":"CRLF",
 "file_lines":2201,"old_string_lines":3,"match_mode":"normalized",
 "match_flags":{"normalize_eol":true,"expand_tabs":4,"trim_trailing_space":true,"ignore_leading_whitespace":false},
 "nearest":[{"line":890,"score":0.93,"preview":"→→return { ok: true, rows: 0 };↵"}, ...],
 "hint":"疑似 Tab/空格差异：第 890 行行首为 Tab（已按 expand_tabs 归一仍未命中）。最相近的是第 890 行（相似度 0.93），建议改用 edit_lines(file_path=\"...\", offset=890, limit=3) 一次成功。"}
```

- `preview` 空白符可视化：行首 / 行尾空格 → `·`，Tab → `→`，行尾 → `↵`。
- `nearest` 固定 Top-3：先用旧文本首行做 Bigram 预筛，再对候选块整体精算；相似度 ≥0.8 但未命中时额外提示「疑似行首缩进差异，可加 `ignore_leading_whitespace=true`」。
- `hint` 必给一条可执行建议（含具体 `edit_lines(offset, limit)` 行号）。

## 5. R4：成功回吐截断

- diff 只保留变更区前后 **±5 行**，总字符预算硬限制；`edit_file` / `edit_lines` 成功输出**严格 ≤ 2000 字符**。
- 超限时追加 `[truncated: true]` 标记，并提示用 `read_file(offset, limit)` 查看完整区间；diff 生成器自身也带预算，超限提前返回（不做超大字符串拼接）。

## 6. R5：高危锚点拦截

- `replace_all=true` 且 `old_string` 去空白后长度 < 8 → 直接拒绝，错误码 `ANCHOR_TOO_SHORT`，并回带「文件内命中 N 处」与替代方案（加长锚点 / `edit_lines`）。
- 复盘点：need13 实战中 `},` + `replace_all` 一次污染 183 处。

## 7. 验收结果（A1–A7）

测试工程：`tests/HaloAgent.Tests`，新增 `Stage14EditFileToolTests`。

| 验收项 | 用例 | 结果 |
|---|---|---|
| A1 hostBridge 式 mock 区 6 处改动（CRLF + Tab + 超长单行）在 `normalized` 下一次成功；同一锚点在 `exact` 下仍失败 | `A1_HostBridgeLike_MockRegion_SixEdits_OneShot` | 通过 |
| A2 Tab 文件 / CRLF 文件 / 超长单行（>400 字符）各 1 例一次成功 | `A2_TabFile_CrlfFile_LongLine` | 通过 |
| A3 故意错 1 个字符 → Top-3 行号 + 空白符可视化 + 可执行建议（JSON 逐字段校验） | `A3_NotFound_ReturnsStructuredDiagnostic` | 通过 |
| A4 `},` + `replace_all=true` 被拒绝，错误码 `ANCHOR_TOO_SHORT`，文件不变；长锚点放行 | `A4_ShortAnchor_WithReplaceAll_Rejected` | 通过 |
| A5 成功回吐 ≤ 2000 字符（含 `truncated` 标记） | `A5_SuccessOutput_IsClamped` | 通过 |
| A6 `edit_lines(offset, limit)` 在 CRLF/Tab 文件上逐字节正确（换行风格不变、仅目标行变化）；纯插入（limit=0） | `A6_EditLines_ByteExact_On_CrlfTabFile` | 通过 |
| A6+ 附加：hash 校验拒绝/放行、空文本保护、越界保护、显式删除 | `A6b_EditLines_HashAndGuards` | 通过 |
| A7 老调用（只传 4 个旧参数）行为不变：唯一匹配替换、多处命中仍报 `matched`、长锚点 `replace_all`、未命中仍失败 | `A7_LegacyFourParams_BehaviourUnchanged` | 通过 |
| A7+ 附加：`regex` 模式与 `ignore_leading_whitespace` 兜底可用 | `A7b_RegexAndIgnoreLeadingWhitespace` | 通过 |
| 非功能 · 性能：1MB 文件 `normalized` 命中 < 200ms、3000 行文件 `nearest` 搜索 < 500ms | `Perf_NormalizedMatch_And_NearestSearch` | 通过 |

回归（A7 的「历史用例行为不变」）：`dotnet test tests/HaloAgent.Tests/HaloAgent.Tests.csproj -c Debug` **全绿**（Stage01–Stage13 全部历史用例；仅 `Stage05` 的内置工具数量断言按需从 10 更新为 11）。

## 8. 已知限制 / 后续可选项

1. **前端卡片未识别 `edit_lines`**：`src/Web.Client/src/components/chat/ToolCallCard.vue` 的图标 / 标题 / 参数 diff 映射仍只覆盖 `edit_file` / `write_file`，`edit_lines` 会落到通用 Wrench 卡片（功能不受影响）。改动需重新构建前端产物（`build-dist.cmd`），故本期未纳入网关交付范围。
2. `regex` 模式的零长度匹配会被拒绝（无法确定替换区间），需在模式中包含实际文本。
3. `ignore_leading_whitespace=true` 时整行缩进交由 `new_string` 决定（不保留原缩进），属预期语义；默认关闭。
4. `edit_file` 的 `new_string` 仍允许为空（= 删除命中区间，历史行为）；`edit_lines` 的空文本必须 `allow_empty=true`。

## 9. 回滚

- 参数级回滚：调用方显式传 `match_mode: "exact"` 即回到历史严格匹配语义（`normalize_eol` 仍默认 true，与旧行为一致）。
- 代码级回滚：新增文件（`EditMatchEngine` / `EditDiffBuilder` / `EditLinesTool`）可整体删除，`EditFileTool` 单文件回退历史版本，并撤销 `ToolRegistry` 的两行注册与 `Stage05` 的数量断言即可；不涉及数据迁移，不写业务文件。
