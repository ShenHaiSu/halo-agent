# 04 正文+Tool+正文+Tool 交错收纳与轮次组装

> 本篇是重构的核心：如何把 `正文 ⇄ 工具 ⇄ 思考` 的真实到达序不丢失地呈现，又不让界面碎成一地卡片。
> 现状三桶丢序实证见 `00 §2.1`；类型见 `01 §3`。

## 1. 问题具象：三种交错形态

```text
A. 单 response 多工具（并行 burst）：  [正文1] -> [tool1, tool2, tool3 同 response] -> [正文2]
B. 多 response 串行（ReAct 循环）：    [正文1] -> [tool1] -> [正文2] -> [tool2] -> [正文3]
C. 思考穿插：                          [思考1] -> [正文1] -> [tool1] -> [思考2] -> [正文2]
```

- 现状对 A 尚可（单 TurnGroup Content+工具舱），对 B 把 `正文2` 与 `tool1` 割裂（多 TurnGroup 或单 content 拼接），对 C 把思考与工具压进同一舱。
- 目标：三种形态统一按 `rowId` 顺序渲染 `blocks[]`，A 的工具自动合并为 1 组（`03`），B/C 的每个交错点都是独立块，可逐段复制。

## 2. 组装算法：assembleBlocks（纯函数）

```ts
export function assembleBlocks(visible: ProjectionRow[]): WorkBlock[] {
  const blocks: WorkBlock[] = [];
  let toolBuf: ProjectionRow[] = [];
  const flushTools = () => {
    if (!toolBuf.length) return;
    for (const g of groupToolRows(toolBuf)) blocks.push({ bkind: 'toolGroup', group: g });
    toolBuf = [];
  };
  for (const r of visible) { // visible 已按 rowId 排序
    if (r.kind === 'toolCall') { toolBuf.push(r); continue; }
    flushTools();
    if (r.kind === 'assistantText') blocks.push({ bkind: 'text', row: r });
    else if (r.kind === 'reasoning') blocks.push({ bkind: 'reasoning', row: r });
    else if (r.kind === 'subagent') blocks.push({ bkind: 'subagent', row: r });
  }
  flushTools();
  return blocks;
}
```

- 输入 `visible` 来自 `buildTurnUnits` 已过滤（空 reasoning/EnterPlanMode/空终态文本已剔）。
- `CUA 分组`（同 `assistantResponseId` 的 text/tool/reasoning 归一组展示边框）在渲染层做视觉分组，不改变 `blocks` 顺序：`TurnUnitView` 按 `assistantResponseId` 分段加 `response-divider`，组内块仍保序。

## 3. 收纳策略：什么折叠、什么常显

| 块 | 默认 | 收起态露什么 | 展开挂载什么 |
|---|---|---|---|
| `text`（正文） | 常显 | 全文 Markdown（流式增量，`05`） | —（无折叠；超长 >8000 字时尾部“展开全文”按需，见 §6） |
| `reasoning` | 收起 | 摘要行（`02 §4`） | `MarkdownLite` + 复制 |
| `toolGroup` | 收起 | 组头汇总（`03 §3`） | 组内每卡 header；每卡 body 再按需展开+视口懒挂载 |
| `subagent` | 收起 | `label + status + done/total` | 子会话跳转 + 摘要 |
| `tailMarkers` | 常显小字 | 轮尾灰字单行 | — |

- 禁止“思考+工具合舱”（现状 `turn-thinking-tools` 删除）。每块独立折叠，用户可只看思考不看工具，反之亦然。
- 轮内首块若为 `toolGroup`（无前言直接调工具），组头前加一行灰色 hint“模型直接调用工具，无前言”。
- 连续两 `text` 块相邻（`Continue` 复用期的过渡形态，`08` 未落地前可能出现）渲染为两段落，中间无 divider；`08` 落地后后端保证同 turn 紧邻 text 合并为单行，前端无需处理。

## 4. 渲染分发：WorkBlockView（替代 TurnGroup 三桶模板）

```vue
<!-- TurnUnitView.vue -->
<UserBubble v-if="unit.userInputs.length" :rows="unit.userInputs" />
<div class="work-blocks">
  <template v-for="b in unit.blocks" :key="blockKey(b)">
    <TextBlockView v-if="b.bkind==='text'" :row="b.row" :hide-actions="!isLastTextBlock(b)" />
    <ReasoningBlock v-else-if="b.bkind==='reasoning'" :row="b.row" />
    <ToolGroupCard v-else-if="b.bkind==='toolGroup'" :group="b.group" />
    <SubagentBlock v-else-if="b.bkind==='subagent'" :row="b.row" />
  </template>
</div>
<TailMarkers :rows="unit.tailMarkers" />
```

- `blockKey`：text/reasoning/subagent 用 `rowId`，toolGroup 用 `gkey`。禁止 `index` key。
- 每个叶子 `memo` 化（`defineComponent + memo` 或 `v-memo[rowId+state+len]`），父级 `TurnUnitView` 重渲染时只有引用变化的块重绘（依赖 `projectionStore` 引用共享，`05 §2`）。
- 分发组件内无条件 hook（`zcodeDocs/06 §1` 纪律）；`ReasoningBlock` 空流式 `return null` 在 hook 之后。

## 5. 动作行（复制/fork/点赞）粒度

- 现状 `hideActions` 按“仅末轮显示”（`MessageTimeline L296-304`），复制内容为整轮合并（`mergeTurnText`）。保留，但粒度细化：
  - 每 `text` 块右上角 hover 显示“复制本段”；轮尾最后一段另显示“复制整轮 / fork / 反馈”（`RowActions` 组件，`hideActions = isLastTextBlockInLastTurn`）。
  - `isLastTextBlock(b)` 由 `TurnUnitView` 裁决（同 unit 内最后一个 `bkind=text`），`isLastTurn` 由时间轴裁决（权威 `turnHeader`，过渡期 `lastTurnKey`，见 `06`）。
  - 整轮复制口径：`userInputs.text + blocks 按序（reasoning 前缀【思考】+ text 原文 + toolGroup 内每卡 output 截断 500 字）`，复用 `mergeTurnText` 但数据源改 `blocks`。

## 6. 超长收纳（防单块撑爆）

- `text` 块 >8000 字终态：尾部截断 + “展开全文（N 字）”按钮，展开前渲染前 8000 字（`MarkdownStream` 支持 `limit` prop，内部 `slice` 到段落边界不断 table）。
- `tool output` >6000 字：卡内截断 + “复制完整输出”按钮（现状 `truncatedOutput 8000` 保留，阈值统一 6000，见 `03`）。
- `reasoning` >4000 字：预览行 + 展开后内部滚动 `max-h-96 overflow-y-auto`（单块内滚动，不嵌套时间轴滚动；工具组内滚动同理，组内容滚动只在展开态）。

## 7. 右键与锚点

- 每块根节点 `data-row-id`（toolGroup 用 `data-gkey` + 内卡各 `data-row-id`），`data-kind`，框选 `data-selectable` 放稳定 DOM 层（`MarkdownStream` 外层 wrapper，`zcodeDocs/06 §2` 纪律）。
- 右键 `openMenu` 按块类型：text/reasoning 走文本复制；toolGroup 走“复制组内全部输出”；subagent 走“跳转子会话”。

## 8. 迁移步骤（现状 TurnGroup → TurnUnit）

1. 新建 `buildTurnUnits.ts + WorkBlockView/TurnUnitView`，`MessageTimeline` 加 flag `useTurnUnits`（`localStorage zcode-turn-units=1`）双跑：flag 开走新组装，关走旧 `buildTurnGroups`。
2. 对照测试：同 `rows` 下新旧工具数/文本总长一致（顺序除外），跑通后默认开。
3. 删合舱模板 L75-97、删 `hiddenToolCallIds` 去重（`06` 删双写后自然无双影）、删 `turnMergedTextOf` 旧数据源（换 blocks 口径）。

## 9. 验收

- [ ] A/B/C 三形态各有 fixture，渲染顺序与 `rowId` 一致，截图对照通过。
- [ ] 折叠默认态首屏高度对比旧版下降（工具 burst 场景）。
- [ ] 复制粒度（本段/整轮/组输出）正确；右键菜单 kind 全覆盖。
- [ ] 双跑 flag 下新旧工具数一致；删旧模板后无回归。
