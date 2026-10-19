# 02 思维链（Reasoning）渲染规范：合理展示思考内容

> 现状痛点见 `00 §4`。本篇给出思考块的出现规则、折叠策略、流式行为、计数与交互，全部可直接落为组件 props。

## 1. 设计原则

1. **思考是第二信息，不是正文附庸**：默认收起，不挤占正文与工具空间；但收起态必须给足摘要，让用户愿意点开。
2. **流式可见**：思考流式期即渲染（纯文本流），不等待 `complete`。持续展开会挤压空间，所以流式期收起态显示“正在思考…+已收 N 字”，点开才看直播。
3. **空即无**：`text.trim()===''` 的思考行永不渲染（组装层过滤，`01 §3`），`streaming` 空行返回 `null`（hook 之后 early return，`zcodeDocs/06 §5` 纪律）。
4. **思考用轻 Markdown**：复用正文 Markdown 管线但关闭重型插件（无表格重排、无高亮 `highlightAuto`，只做加粗/列表/代码行内），避免思考大段代码拖慢正文。

## 2. 出现与过滤规则

```ts
// 组装层（buildTurnUnits.visibleWorkFilter）
if (row.kind==='reasoning' && (row.text??'').trim()==='') return false;
// 流式空行组件内二次兜底（hook 之后）
if (row.state==='streaming' && (row.text??'').length===0) return null;
```

- 后端 `StreamToRowProjector.OnReasoningDelta` 空直接 return（已正确），前端再滤一次防历史脏数据。
- 同 `assistantResponseId` 的多 `reasoning` 行（如多 response）在组装层**不合并**，各为独立 `WorkBlock{bkind:'reasoning'}`，按 `rowId` 插在对应位置（保序，见 `04`）。只有一个例外：同一 response 内被 `BeginResponse` 切开的首尾空行由后端合并，前端不处理。

## 3. 组件：ReasoningBlock.vue（新建）

```vue
<!-- props -->
<script setup lang="ts">
const props = defineProps<{
  row: ProjectionRow;            // kind=reasoning
  defaultCollapsed?: boolean;    // 缺省 true；流式期强制收起态（除非用户已手动展开）
  autoCollapseKey?: string|null; // 终态翻转时自动收起（streaming->complete）
}>();
</script>
<template>
  <div class="reasoning-block" :data-row-id="row.rowId" data-kind="reasoning" :data-status="row.state" data-selectable>
    <button class="reasoning-head" @click="toggle" :aria-expanded="expanded">
      <BrainCircuit /> <span>思考过程</span>
      <span class="meta">{{ summary }}</span>   <!-- 见 §4 -->
      <ChevronDown :class="{ rotated: expanded }" />
    </button>
    <div v-if="expanded" class="reasoning-body">
      <MarkdownLite :text="row.text" :status="row.state" />  <!-- 轻量 markdown，见 §5 -->
      <div class="reasoning-foot">
        <span>{{ tokenEst }} tokens · {{ duration }}</span>
        <button @click="copy">复制思考</button>
      </div>
    </div>
    <div v-else class="reasoning-preview">{{ previewLine }}</div> <!-- 首行截断 120 字 -->
  </div>
</template>
```

交互纪律：

- `expanded` 缺省 `false`；`localStorage reasoning-expand-<turnId>` 记忆用户手动选择（展开后切卡保留，`MessageTimeline.expandedTurns` 现状为内存 `reactive`，保留但 key 改为 `rowId` 粒度而非 `turn key`）。
- 流式期（`state=streaming`）若用户未手动展开，保持收起 + 头部 pulse 动画；若用户已展开，保持展开直播（不强制收起打断阅读）。
- 终态（`complete/interrupted/failed`）到达时若用户从未手动操作，自动收起（`autoCollapseKey` 翻转触发）。
- `early return null` 必须在所有 `computed/watch` 之后（hook 纪律）。

样式：沿用现状 `bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70`，但从“整舱”降级为“单块”左边框；舱级背景删除（`04` 拆舱）。

## 4. 摘要行文案（收起态唯一信息）

```text
流式： 思考中… · 已收 {chars} 字（约 {tokens} tokens）· {tools} 个工具已调用
终态： 思考 {chars} 字（约 {tokens} tokens）· 耗时 {ms} · 点击展开
中断： 思考已中断 · {chars} 字 · 点击查看已收部分
失败： 思考失败 · 点击查看
```

- `chars = row.text.length`，`tokens = estTokens(row.text)`（复用 `agentStore.estTokens`，但移入 `utils/tokens.ts`，渲染 store 不再计算）。
- `耗时` 取同 `assistantResponseId` 的首尾行时间差（行无时间戳时显示 `-`，不猜；后端补 `extra.timing` 见 `08`）。
- 预览行取 `text` 首非空行截 120 字，代码围栏/表格符号转空格，避免半截 markdown 符号污染摘要。

## 5. 渲染管线（轻 Markdown）

- 新建 `MarkdownLite.vue`：复用 `markdown-it` 实例但配置 `highlight: () => ''`（不做代码高亮，思考内代码只套 `pre` 素样式），`linkify:true, breaks:true`，容错规则复用 `MarkdownStream.tolerantMarkdown`（补未闭合围栏）。
- 流式节流：`row.text` 变化经 `useRafThrottle(text, 64ms)` 再进 `md.render`（见 `05 §3`），思考帧率减半（正文 30ms，思考 64ms），错峰。
- 终态一次重排：`state` 翻为 `complete` 时全量 `md.render` 一次（与正文同一渲染函数，保证终态一致）。

## 6. 复制与右键

- 思考块独立复制（只复制本块 `text`），整轮复制（`mergeTurnText`）保留 `【思考】` 前缀拼接（现状 `toolCallRowToCard.mergeTurnText` 保留，数据源改行）。
- 右键菜单 `MessageContextMenu` 新增 `target.kind='reasoning'`，`id=rowId`，`text=row.text`（`MessageTimeline.openMenu` 扩展，`historyAnchor.copyTextToClipboard` 复用）。

## 7. 从现状迁移（文件级）

| 现状 | 迁移 |
|---|---|
| `MessageTimeline L75-97` 思考+工具合舱 | 拆为 `ReasoningBlock` + `ToolGroupCard` 两个 `WorkBlock` 叶子，合舱容器删除 |
| `L89` 纯文本 `div` | 换 `MarkdownLite` |
| `expandedTurns[key=turnKey]` | 换 `expandedRows[rowId]` + `expandedGroups[gkey]` 两套 key（`useBlockExpansion.ts` 新建 composable） |
| `turnMergedTextOf` 思考前缀 | 保留，数据源改 `TurnUnit.blocks` |

## 8. 验收

- [ ] 空思考永不占位（含流式空行）；单轮多思考块保序。
- [ ] 流式期收起态字符数单调递增，无闪烁；展开态直播不丢字。
- [ ] 终态自动收起 + 摘要 token/耗时正确；复制粒度（单块/整轮）正确。
- [ ] hook 纪律：增删 reasoning 行不白屏（条件 hook 扫描通过）。
