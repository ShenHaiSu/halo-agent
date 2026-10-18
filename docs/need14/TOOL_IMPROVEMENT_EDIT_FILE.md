# edit_file 工具改进需求与方案（源自 need13 实战复盘）

> 状态：需求稿（待别的会话实现）。背景：2026-10-18 need13 施工中，`edit_file` 在大文件（尤其 `src/Web.Client/src/bridge/hostBridge.ts`，CRLF、2201 行、单行 400+ 字符、Tab/混合缩进）上反复 `old_string not found`，导致大量重试与 token 浪费。后端小文件（LF、空格缩进，如 `AgentBehaviorView.vue`）一次过。结论：工具侧严格字节级精确匹配是主因，需改进为更宽容 / 更智能的参数接受方式。

## 1. 问题复盘（证据链）

### 1.1 高频失败模式

| # | 现象 | 证据 |
|---|---|---|
| F1 | 带缩进的多行 `old_string` 100% 失败 | `hostBridge.ts` mock 区（880–1730 行）任何含行首缩进的多行匹配均报 `old_string not found`；顶格行（`export interface`）一次成功 |
| F2 | 单行行尾片段 100% 成功 | `maxIterations: 25,`、`lastRejection: null } }; }`、`mockAgentDefaults` 等无缩进锚点全部一次成功 |
| F3 | `replace_all` 误伤 | `},` 一次污染 183 处，被迫 `git checkout` 回滚整文件；`mockAgentDefaults` 6 处同改把 mock/真桥接搅在一起 |
| F4 | 成功输出太贵 | 每次成功回吐 40k–50k 字符全量 diff，失败一次 ≈ 调用 + 报错 + 重读，token 烧得比写代码多 |
| F5 | 错误信息无用 | 永远只有 `old_string not found in file`，不给行号、不给相似行、不给空白符差异，只能盲猜 |
| F6 | 补丁越打越乱 | 被迫引入 `M1732`、`mock-sub-ok2`、`DEL1733` 等标记锚点，文件残留垃圾标记 |

### 1.2 根因分析

1. **严格字节级精确匹配**：`old_string` 差 1 个字符（1 个 Tab vs 4 空格、`\r\n` vs `\n`、行尾 1 个空格）即全拒。
2. **缩进字符不一致**：`read_file` 渲染后看着是空格，落盘可能是 Tab；agent 复制走的文本与落盘字节对不上。
3. **换行符不一致**：CRLF 文件跨多行匹配时，`read` 回来的 LF 文本与落盘 CRLF 对不上。
4. **超长单行**：mock 区单行 400+ 字符，`old_string` 稍长即因不可见字符失败。
5. **无行号编辑能力**：只能字符串匹配，没有按行号替换的逃生通道。
6. **成功回吐无截断**：全量 diff 动辄几万字符，直接放大 token 消耗。

### 1.3 非工具侧的次要失败（顺带记录，不在本次改造范围）

- `exec_command` 用了 `head`/`tail`/`cat`（`cmd.exe` 没有）与 `runner: python`（本机无 Python）。建议网关侧做 runner 自动纠偏提示，但优先级低于 `edit_file`。

## 2. 改进目标

1. **成功率**：同一语义改动，一次成功率从当前大文件约 20% 提升到 95% 以上。
2. **省 token**：失败重试次数下降 80%；成功回吐截断到 2k 字符以内。
3. **可诊断**：失败时 1 次返回即可定位（行号 + 空白符可视化 + 最相似候选）。
4. **向后兼容**：默认行为保持 `exact` 可选；老调用不改参数仍可用。

## 3. 功能需求

### R1. 宽容匹配模式（核心）

`edit_file` 新增参数（全部可选，有默认值，老调用零改动）：

```ts
edit_file({
  file_path: string;      // 不变
  old_string: string;     // 不变
  new_string: string;     // 不变
  replace_all?: boolean;  // 不变

  // 新增：
  match_mode?: 'exact' | 'normalized' | 'regex';  // 默认 'normalized'
  normalize_eol?: boolean;        // 默认 true：比对前 \r\n→\n、\r→\n
  expand_tabs?: number;           // 默认 4：比对前 Tab 按 N 空格展开（仅用于比对，不改落盘）
  trim_trailing_space?: boolean;  // 默认 true：忽略行尾空格差异
  ignore_leading_whitespace?: boolean; // 默认 false：为 true 时行首缩进只比“相对缩进”
});
```

匹配流程（`normalized`）：

```text
1. 按 normalize_eol 统一换行；
2. 按 expand_tabs 展开 Tab；
3. 按 trim_trailing_space 去行尾空格；
4. 再做子串查找；命中则按原落盘字节做替换（只换目标区，不动全文件换行/缩进风格）；
5. 仍未命中 → 走 R3 的诊断返回，不直接报错。
```

### R2. 按行号编辑（逃生通道，强烈建议）

新增工具（或 `edit_file` 的互斥参数组，二选一实现即可，推荐独立工具）：

```ts
edit_lines({
  file_path: string;
  offset: number;      // 1-based 起始行（与 read_file 一致）
  limit: number;       // 覆盖行数（= old 行数）
  new_text: string;    // 替换后的完整文本（LF 即可，落盘时按原文件 EOL 转换）
  expected_hash?: string; // 可选：调用方传 read 到的旧文本 hash，防并发改动；不符则拒绝
});
```

语义：直接按行号替换，不做任何字符串匹配，天然免疫缩进/换行问题。`hostBridge.ts` 这类文件以后一律走此通道。

### R3. 失败诊断增强（省 token 的关键）

`old_string` 未命中时，返回结构化诊断而非一句话：

```json
{
  "ok": false,
  "code": "OLD_STRING_NOT_FOUND",
  "file": "src/Web.Client/src/bridge/hostBridge.ts",
  "file_eol": "CRLF",
  "file_lines": 2201,
  "nearest": [
    { "line": 890, "score": 0.93, "preview": "·····version: 1, execDefault···（Tab 显示为 →，行尾空格显示为 ·）" },
    { "line": 1598, "score": 0.81, "preview": "..." }
  ],
  "hint": "疑似 Tab/空格差异：第 890 行行首为 Tab，已按 expand_tabs=4 归一仍未命中，建议改用 edit_lines(offset=890, limit=3)"
}
```

要求：

- `preview` 必须做空白符可视化（Tab→`→`、行尾空格→`·`、CRLF 行尾→`↵`）。
- 必须给出 Top-3 最相似行号与相似度。
- 必须给一条可执行建议（换哪种模式 / 改用 `edit_lines` 的具体行号）。

### R4. 成功回吐截断

- 成功时 diff 只返回变更区前后各 5 行，总计超过 2000 字符则截断并注 `truncated: true`。
- 当前全量回吐（动辄 50k 字符）必须禁止。

### R5. 高危锚点拦截

- 当 `old_string` 去空白后长度 < 8 且 `replace_all=true` 时（如 `},`、`};`），直接拒绝并提示：
  `ANCHOR_TOO_SHORT：锚点过短且 replace_all=true，本次已拒绝，请加长锚点或改用 edit_lines`。
- 防止本次 `},` 污染 183 处的事故重演。

## 4. 非功能需求

- 性能：`normalized` 匹配对 1MB 以下文件 < 200ms；`nearest` 搜索对 3000 行文件 < 500ms。
- 安全：`edit_lines` 的 `expected_hash` 机制防并发覆盖；拒绝跨文件、拒绝空 `new_text` 误删（需显式 `allow_empty=true`）。
- 兼容：不改现有 4 个参数的语义与默认值；`match_mode` 缺省走 `normalized`（比 `exact` 更宽容，但对老用户安全——`normalized` 下能命中的，`exact` 下语义一致）。

## 5. 验收标准

- [x] A1：用本需求稿 §1 的 `hostBridge.ts` mock 区 6 处改动做回归：同一 `old_string` 在 `normalized` 下一次成功。（`A1_HostBridgeLike_MockRegion_SixEdits_OneShot`）
- [x] A2：Tab 文件 + CRLF 文件 + 超长单行（>400 字符）各 1 个用例，一次成功。（`A2_TabFile_CrlfFile_LongLine`）
- [x] A3：故意给错 1 个字符的 `old_string`，返回含 Top-3 行号 + 空白符可视化 + 可执行建议。（`A3_NotFound_ReturnsStructuredDiagnostic`）
- [x] A4：`},` + `replace_all=true` 被拒绝，错误码为 `ANCHOR_TOO_SHORT`。（`A4_ShortAnchor_WithReplaceAll_Rejected`）
- [x] A5：成功回吐 ≤ 2000 字符（含截断标记）。（`A5_SuccessOutput_IsClamped`）
- [x] A6：`edit_lines(offset, limit)` 在 CRLF/Tab 文件上逐字节正确（换行风格不变，仅目标行变化）。（`A6_EditLines_ByteExact_On_CrlfTabFile` + `A6b_EditLines_HashAndGuards`）
- [x] A7：老调用（只传 4 个旧参数）在 200 个历史用例上行为不变。（`A7_LegacyFourParams_BehaviourUnchanged` + 全量回归 `dotnet test` 全绿）

> 实施与验收明细见 [`01-implementation-and-acceptance.md`](./01-implementation-and-acceptance.md)。

## 6. 建议实现顺序（给接手会话）

```text
Step 1：先做 R4（回吐截断）+ R5（高危拦截）—— 改动最小，立刻止血省 token。
Step 2：做 R1（normalized 匹配三件套：EOL/Tab/行尾空格）—— 解决 80% 的失败。
Step 3：做 R3（诊断返回）—— 把剩余 20% 的排查成本打下来。
Step 4：做 R2（edit_lines）—— 一劳永逸，大文件以后走行号。
Step 5：跑 A1–A7 验收，全绿合闸。
```

## 7. 风险与回滚

- 风险：`normalized` 默认开启可能让原本 `exact` 下“故意不命中”的调用意外命中。缓解：`match_mode` 保留 `exact` 选项；R5 拦截短锚点；`edit_lines` 要求 `expected_hash` 时可防误改。
- 回滚：任一 Step 出问题，只回滚该 Step 对应的网关改动，工具参数缺省即回 `exact` 行为，会话侧无残留（不写业务文件）。

## 8. 附：本次实战留下的对照样本（可直接做测试用例）

- 必过（单行行尾锚点）：`maxIterations: 25,` → 加 7 个子代理字段；`lastRejection: null } }; }` → 追加 mock 方法。
- 必败（旧 exact 下）：mock 区任何含 6–8 空格行首缩进的多行 `old_string`（如 `async getUsageDbStatus()` 整块）。
- 高危：`},` + `replace_all=true`（曾污染 183 处，已 `git checkout` 回滚）。
- 残留标记（如接手时仍在）：`// mock-sub-ok2`、`// mock-obj-end // DEL1733`（在 `hostBridge.ts` 1720–1740 行附近，属本次绕行产物，工具修好后应清掉重做）。
