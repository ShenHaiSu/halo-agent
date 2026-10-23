# need21-05 分阶段施工清单（照抄执行）

> 按 P0→P4 顺序施工，每阶段独立提交、独立验收、独立回滚。命令均在 `develop/src/Web.Client` 下执行（另有注明除外）。

---

## P0 基线（0.5d，不改行为）

- [ ] 1. 探针：在 `stores/projectionStore.ts` `onFrameCoalesced` 入口记 `performance.now()` 差值 → `window.__perf.timeline = { frames, lastMs, maxMs }`（抄 `MarkdownStream markPerf` 模式，`import.meta.env.DEV` 门控）。
- [ ] 2. 基线数据：准备 3 个会话（空 / 50 轮 / 200 轮，用现有 `tests/HaloAgent.Tests` 数据或手工刷），同一脚本（工具×3 + 3k 字回答）各播一遍，记录 `__perf.markdown.count/lastRenderMs`、`__perf.timeline.maxMs`、长任务数（Performance 面板）。
- [ ] 3. 单测加锁（`src/utils/__tests__/buildTurnUnits.test.ts` 追加，不改旧用例）：
  ```ts
  // 同 turn 内 blocks 只能按 rowId 升序出现；toolGroup 永不插到 text 之前
  // canMerge 跨 assistantResponseId 永不合并；20 上限拆组；60s 窗回退
  ```
- [ ] 4. 门禁：`npx vue-tsc -b && npx vitest run src/stores/__tests__ src/utils/__tests__` 全绿。
- [ ] 提交：`chore(need21): P0 perf probes + order tests`。

## P1 单轨化（1-2d，核心破坏点）

- [ ] 1. `MessageTimeline.vue`：
  - [ ] 删除 L20-72 旧分支整段（含 `timelineItems/toolKeyOf/toCard/turnMergedTextOf` 相关 import，保留 `useMarkdownStream/renderMarkdown` 供 pill 外的终态？不——终态走 `MarkdownStream` 组件，函数版 `renderMarkdown` 一并删除）。
  - [ ] 删除 L73-89 active 正文区（`toolCards` 循环 + `streamingText` 块），新建 `ActiveTurnPill.vue` 挂载到原容器位置，根节点双 testid（`active-turn-container` + `active-turn-pill`）。
  - [ ] 数据源切换：`persistedMessages`/`legacy` 相关删除，`turnUnits = computed(() => buildTurnUnits(projectionStore.rowsOf(activeSessionId)))`（经 04 §4 增量函数，P1 先接全量签名，P2 再加缓存）。
  - [ ] `showActiveContainer` 删除，pill 自带空态门（无 streaming 行且 `!running` 不渲染）。
  - [ ] 右键菜单文本源：`turnMergedTextOf` → `mergeTurnTextFromBlocks`（`registry` 已有，`unitTextOf` 已用该口径 L165-167，复用它）。
  - [ ] 三个 flag 读取删除（`zcode-turn-units` L142-146、`zcode-markdown-stream` 在 legacy 内已随旧分支删除、`zcode-renderer-registry` 在 workspaceStore），按新值写死。
- [ ] 2. `agentStore.ts`：删除 `streamingText/streamingReasoning/toolCards` 字段与 L397-414 累积分支，`refreshFromProjection` 整段删除，三处调用改为 `touch`；`llm:stream-event` 只留 4Hz `liveEst`（见 04 §2.2）。
- [ ] 3. `workspaceStore.ts`：删除 `syncMirror`（L422-431）及 L451/L531/L576 三处调用；`MessageTimeline` 的 `storeToRefs(sessionMessages)` 改为订阅 `projectionStore`。
- [ ] 4. 新增 `composables/useLiveStatus.ts` + `components/chat/ActiveTurnPill.vue`（规范见 03 §5）。
- [ ] 5. 验收（见 06 §2 P1 矩阵）：resp/chat 各一轮、推理只出现一次、复制逐字 diff、`终止`按钮可用。
- [ ] 提交：`feat(need21): P1 single-track render + live pill`（回滚即 revert 本提交，旧分支代码仍在 git 历史）。

## P2 行表增量（2-3d）

- [ ] 1. `projectionStore.ts`：快照改 Map（04 §1.1），`rowsOf` 同 seq 返回同一数组引用；`projection:applied` 追加 `changedTurnIds`（04 §1.2）。
- [ ] 2. `buildTurnUnits.ts`：加模块缓存（04 §4），`visibleWorkFilter/canMerge/groupToolRows/assembleBlocks` 零改动。
- [ ] 3. `llm:stream-event` rAF 合并（04 §5 前端部分）；`useLiveStatus` 2Hz。
- [ ] 4. 可选后端：`AgentOrchestrator.cs` L498-501 节流全量化（04 §5），单独提交 `perf(gateway): throttle all ToolCallArgs to 150ms`，前后端可独立回滚。
- [ ] 5. 验收：06 §3 性能预算（单帧 <5ms 等）；`vitest run` 全绿。
- [ ] 提交：`perf(need21): P2 map rows + incremental turn units + event batching`。

## P3 渲染管线（2d）

- [ ] 1. `MarkdownStream.vue` 两档（03 §1）：删 streaming markdown 分支，加纯文本档 + 终态 hash 缓存；`useRafThrottle` 在本组件去引用（文件保留，其他组件仍可用）。
- [ ] 2. 新建 `composables/useInViewport.ts`（03 §2.1），`TurnUnitView` 根包装懒挂载（占位估算 48px/块、最小 96px；锚点注册保留）。
- [ ] 3. `ToolCallCard` 终端流式上限 2000 字（03 §3）；`useToolArgs` 加 256B 长度门。
- [ ] 4. `useTimelineScroll.ts` 贴底门控 + 删 200ms timer（03 §4）。
- [ ] 5. `ReasoningBlock.vue` 数据源切换（03 §6，字段同名，小改）。
- [ ] 6. 验收：3k 字回答流式无 >200ms 长任务；`headerSig` 单卡翻转 ≤3 次；滚动不丢锚点（need19 跳转用例重跑）。
- [ ] 提交：`perf(need21): P3 two-tier markdown + lazy mount + gated scroll`。

## P4 清理锁死（1d）

- [ ] 1. 删除：`composables/useLegacyTurnGroups.ts`、`utils/turnGroup.ts`、`utils/turnUnits.ts`（`messagesToRows`）、`utils/toolCallRowToCard.ts`（`mergeTurnText` 已收敛到 registry，先确认无其他 import 再删）、`stores/__tests__/coalesce.test.ts` 中与旧链路相关的用例（保留 coalesce 本体用例）。
  - [ ] 删前 `grep -rn "useLegacyTurnGroups\|turnGroup\|messagesToRows\|turnMergedTextOf\|syncMirror\|streamingText\|toolCards" src --include=*.vue --include=*.ts` 必须零命中（测试与注释除外）。
- [ ] 2. `ToolCallCard.legacyToRow` + `card` prop 分支删除（全切 `row` prop）。
- [ ] 3. `vue-tsc -b` + 全量 `vitest` + 手工回归（06 §2 全矩阵）。
- [ ] 4. 文档：本系列 00-07 定稿（状态改为“已施工”），`learnDocs` 加一节迁移说明。
- [ ] 提交：`chore(need21): P4 remove legacy render paths`。

## 提交切分总览

```text
chore(need21): P0 perf probes + order tests
feat(need21): P1 single-track render + live pill
perf(need21): P2 map rows + incremental turn units + event batching
perf(gateway): throttle all ToolCallArgs to 150ms   (可选独立)
perf(need21): P3 two-tier markdown + lazy mount + gated scroll
chore(need21): P4 remove legacy render paths
```
