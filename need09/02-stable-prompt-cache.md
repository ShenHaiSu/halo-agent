# need09-02 稳定低 token 拼接与高缓存命中方案（Skill/Memory 装配线）

> 前置：`01-skill-memory-storage.md`。本篇是全文技术核心，冻结后 03/04/05/06/07 均按此装配。
> 现状证据：`SystemPromptBuilder.BuildCore` 按 `custom(head,stable) → identity(stable) → runtime(stable) → cmdPython(stable) → toolGuide(stable) → ephemeral(非稳定) → custom(tail,非稳定)` 装配，`\n\n` 连接 + 末尾 `\n`；`TokenBudgetService` 高水位 92% 才裁、低水位 70%，原子轮次裁剪；`PrefixCachePredictor` 5 块 `[system.stable, historyPrefix, latest, system.ephemeral, tools]` 拼接对比 + `EWMA(0.3)` 平滑；`LlmGatewayClient` 记录 `prefixHash=SHA16(整包payload)` + `PromptLastSentStore` 记录 `sysHash`；双 Adapter 对 tools 按 `name` 升序稳定排序。

## 1. 设计目标（量化冻结）

| 目标 | 指标 | 手段 |
|---|---|---|
| 稳定性 | 同一会话连续两轮若 Skill/Memory/历史未变，`prefixHash` 必须逐字节一致；`divergenceBlock` 应为 `stable` | 固定顺序 + 固定排序 + 规范化 + 禁止时间戳进稳定区 |
| 低 token | 无 Skill/Memory 时新增 token = 0；有 20 个 Skill 时常驻 ≤ 1500 tokens；常驻 Memory ≤ 2000 tokens | L0 索引常驻 + L1 正文按需（工具拉取） |
| 高命中 | 日常追问（只改 latest user 文本）时 `divergenceBlock=latest`，稳定前缀命中（厂商 cached 占比最大化） | stable 块放最前、ephemeral 放最后、tools 放末尾（已是现状，延续） |
| 可解释 | 每次请求 `llm-trace` 可回答“这次为什么没命中” | `predictedHit/divergenceBlock/stableTokens` 三字段（延续 need05-06） |

## 2. 装配线总顺序（冻结，字节级契约）

```
[0] system.stable（稳定，参与缓存）
    ├─ identity（存量）
    ├─ runtime（存量，{{WorkspaceRoot}} 等变量渲染后即稳定，工作区不变则不变）
    ├─ cmdPython（存量）
    ├─ toolGuide（存量，02 追加 Skill/Memory 使用指南 3 行，内容冻结后永不改）
    ├─ skillIndex（新增，稳定）      ← 本篇核心
    └─ memoryPinned（新增，稳定）    ← 本篇核心
[1] historyPrefix（除末条外的历史，逐消息哈希，见 §5）
[2] latest（末条 user/assistant，含图片引用，见 05）
[3] system.ephemeral（非稳定：日期/PythonStatus/SessionHash提示，存量逻辑不动）
[4] tools（按 name 升序，存量逻辑不动；新增 3 工具后仍升序，见 07）
[5] custom(tail)（非稳定，存量逻辑不动）
```

- `custom(head)` 若用户启用仍放最前（存量语义），但文档强烈建议 Skill/Memory 用户不要用 `custom(head)`（会整体后移稳定块，杀命中率）；`PromptSettingsView` 加一行灰字提示即可（本期最小 UI）。
- 任何新增块禁止插入到 `identity` 之前；禁止在稳定区内插入时间、随机数、轮次计数器。

## 3. L0/L1 分级装载（token 核心）

### 3.1 Skill：索引常驻（L0）+ 正文按需（L1）

**L0 skillIndex（稳定区，必然发送）：**

```text
## 5. Available Skills (auto-loaded index; read full body on demand via skill_tool)
- [global] git-helper — 常用 git 流程与冲突处理
- [project] api-convention — 本项目接口命名与错误码规范
(Use skill_tool(action="get", scope="...", name="...") to load full body before applying.)
```

- 每行格式冻结：`- [<scope>] <name> — <summary≤60字>`，按 `scope(global先) → name升序` 排序（注意：不是 priority 排序——priority 只用于超限截断，不用于排序；排序键必须与内容无关，否则改 priority 即抖前缀）。
- `summary` 取 `scope_hint || 首句简介`，超 60 字截断 + `…`（截断规则冻结，避免长度抖动）。
- 空域时整块（含标题）**直接消失**（不留空标题，避免空块占哈希）。
- 超限（>1500 tokens）：按 `priority 高优先保留 + 同 priority 按 name 升序` 截断，末尾加一行 `(…and N more skills omitted; list via skill_tool)`；该行文案冻结。

**L1 Skill 正文（非稳定，按需）：**

- 模型需要用某 Skill 时调 `skill_tool(action=get)`（见 07），正文以 **tool 结果消息**形式进入 `historyPrefix`（自然成为“历史的一部分”，不污染稳定区）。
- `pin=full` 的 Skill（慎用，需用户显式设置）正文也拼入稳定区（放在索引块之后），但受 L0 配额双倍约束且 UI 红字警告（“常驻全文会显著增加每轮费用”）。

### 3.2 Memory：pinned 常驻（L0）+ archived 按需（L1）

```text
## 6. Pinned Memory (project overrides global on same name)
### [project] project-brief
本项目是 Windows 桌面 Agent，外层 WinForms + CefSharp，内层 Vue3……
### [global] user-pref
用户偏好中文回复，代码注释用中文……
```

- 仅 `pin=pinned`（memory）进入稳定区；`auto/archived` 只出现在索引行（与 Skill 索引合并为一行，避免多块）或完全不出现（`archived` 默认不出索引，需 `memory_tool(list)` 发现，极致省 token）。
- 同名去重：Global 与 Project 同名 memory → **Project 覆盖 Global**（整条替换，不合并），`mergedFrom` 只记日志不进提示词（避免元信息抖动）。
- 排序：按 `name升序`（与 Skill 同理，与 priority 无关）；超限截断同 Skill（保留高 priority，截断行文案冻结）。

### 3.3 为什么索引行不用 priority 排序？

厂商 Prompt Cache 是**前缀字节一致**即命中。按 priority/热度排序会导致“改一个 Skill 的优先级 → 全索引行移位 → 整包哈希变 → 缓存全丢”。排序键必须选用**几乎不变的键**（`scope+name`），把易变的 `priority/updated_at` 只用于截断取舍（截断本身已是小概率事件）。

## 4. 规范化与哈希（冻结）

- 单条规范化：`body_norm = body.Replace("\r\n","\n").TrimEnd() + "\n"`；`summary_norm = summary.Trim().Replace("\n"," ")`。
- 块序列化：行间 `\n`，块间 `\n\n`，末尾 `\n`（与存量 `BuildCore` 完全一致）。
- `PrefixHash`：沿用 `SHA16(assembledText)`（`HeaderVariableResolver.ComputeDeterministicHash16`），Skill/Memory 变更自然改变哈希，无需额外版本号进提示词。
- 图片引用（见 05）参与哈希的是**归一化引用字符串**（`image:<sha16(bytes)>:<w>x<h>:<mime>`），不是 base64；重编码/压缩不改变引用即不抖动（`view_image` 归一化+C# 端缓存保证）。

## 5. 与裁剪/预算/预测的协同（精确到函数）

### 5.1 `TokenBudgetService` 扩展

- `EstimateMessageTokens` 新增图片开销（详见 05）：`6(结构) + 文本启发式 + Σ图片固定开销(≈1000/image，可配)`；Skill/Memory 文本沿用存量 `EstimateTokens`（ASCII/3.6 + CJK*1.55）。
- 裁剪优先级（`BuildWithinBudget` 达线后）：`①丢弃最旧图片引用（替换为 `[image omitted]` 占位，保留文字）→ ②丢弃中间 tool 组（存量原子组逻辑）→ ③截断 memoryPinned 超长条（只在极端时，优先保 tool 对完整）`。稳定区 Skill 索引**永不参与裁剪**（裁剪只动 history，不动 system.stable——这是高水位设计的前提）。
- `TruncateToBudget` 高/低水位（92%/70%）**不动**；Skill/Memory 常驻 token 计入 `usedInput` 分子（自然推高水位，无需特殊处理）。

### 5.2 `PrefixCachePredictor` 扩展（5 块 → 7 逻辑块，物理仍按 5 块哈希）

- 逻辑块：`[system.stableWithoutSkill, skillIndex, memoryPinned, historyPrefix, latest, system.ephemeral, tools]`；物理快照仍拼为存量 5 块（把 skillIndex+memoryPinned 并入 `system.stable` 的子哈希），避免改存量 `PrefixSnapshot` 结构；但 `Compare` 的 `DivergenceBlock` 新增取值 `skillIndex/memoryPinned`（可解释性）。
- `BuildSnapshot(systemPrompt, messages, tools)` 签名不动，内部用 `SplitSystem` 切出 `stable/ephemeral` 后，再对 `stable` 做二次切分（按 `## 5./## 6.` 标题行切，标题行文案冻结即切分稳定）。

### 5.3 `LlmGatewayClient` 日志扩展

- `[REQ]` 行追加 `skillL0=<n> skillTokens=<t> memPinned=<m> memTokens=<t2> imgCount=<c>`；`[RES]` 行追加 `hitRate` 已有，无需改。
- `PromptLastSentStore` 的 `sysHash` 改为 `stableHash`（stable 区哈希）+ 新增 `fullHash`，`getPromptDiagnostics` 返回两者对比（“稳定区是否变化”一目了然）。

## 6. SystemPromptBuilder 改动（精确到块）

- `BuildCore(cfg, ctx, skillsSnapshot?, memorySnapshot?)` 新增两个可选参数（缺省即空快照，保证老单测逐字节一致）。
- `toolGuideTemplate` 默认文本追加 3 行冻结文案（`PromptDefaults.ToolGuideTemplate` 常量改，版本号不变）：
  ```
  - Skills: consult `## 5. Available Skills` index first; load body via `skill_tool` before applying; project scope overrides global on same name.
  - Memory: `## 6. Pinned Memory` is authoritative; use `memory_tool` to list/read archived memory; never invent persistent facts.
  - Images: use `view_image` to inspect workspace images; user-pasted images arrive inline in the latest message.
  ```
- `AgentOrchestrator` 轮首快照：`skillsSnapshot = skillMemoryService.GetMergedForPrompt(workspaceRoot)`（与 `agent.json` 快照同一位置，只读一次，见存量 `effectiveMax` 快照代码旁）。

## 7. 未来冗余（本期留位，不实现）

- `skillIndex` 行预留 `v:<version>` 后缀位（本期不拼版本，避免版本抖动杀缓存；未来命中率足够高时可开 `ShowVersionInIndex` 开关）。
- `memory` 预留 `Retrieval:` 块位（未来 RAG 检出的动态记忆放 `ephemeral` 区，不进稳定区，天然不污染缓存）。
- `Modalities:` 能力行预留（未来音频/视频能力位拼在 `toolGuide` 末尾，稳定）。
