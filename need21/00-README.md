# need21 主聊天渲染彻底重构：单轨持久态 + 长会话性能

> 状态：施工方案（待施工，已授权破坏性改进）。
> 作用域：`develop/src/Web.Client` 纯前端为主，后端仅允许事件节流类小改；落盘格式不动。
> 一句话目标：① 砍掉流式第二套渲染，全走持久行定序渲染，彻底消除“正文出现两次 / 工具与正文乱序 / 无换行拼接”；② 长会话（200+ 消息）流式零卡顿，视觉与交互像素级不变。

---

## 1. 背景（一句话）

当前 `MessageTimeline.vue`（239 行）三套渲染并存：旧三桶分支（默认开）+ `active-turn-container`（流式累计串）+ 新 `TurnUnitView`。`agentStore.streamingText` 跨 iteration 累积不清零（只在 `sendPrompt→resetTurnState` 置空，`IterationStarted`/`AssistantMessagePersisted` 都不清，`finishTurn` 才清），旧路径 `showActiveContainer` 恒 `true`，导致流式期持久 turn 与 active 累积串同时可见；逐 token `touch` + 全文 `md.render` + 全量镜像重建造成长会话卡顿。详细证据见 [01](./01-diagnosis-evidence.md)。

## 2. 目标形态

```text
后端帧 → projectionFrameQueue(rAF合并+coalesce) → projectionStore(Map行表)
  → buildTurnUnits(增量, 仅末turn重算) → TurnUnitView(turn级懒挂载)
  → TextBlock(流式纯文本 / 终态一次markdown)
  → ToolGroupCard(Header常驻 / Body进视口才挂载)
  → 轻量pill(spinner+计数, 无body)
```

* 顺序：一切按 `rowId` 升序，`assembleBlocks` 不做任何重排。
* 真相源：唯一 `projectionStore.rowsOf(sid)`；`sessionMessages` 降级为标题/导出/分支专用；`agentStore` 只留遥测与控制。
* 视觉：折叠默认态、`testid`、`data-row-id/kind`、右键菜单、复制口径全部不变。

## 3. 允许的破坏性改动

| # | 改动 | 说明 |
|---|---|---|
| D1 | 删除旧三桶分支 | `MessageTimeline.vue` L20-72、`useLegacyTurnGroups.ts`、`turnGroup.ts` 整套删除 |
| D2 | 删除 active 正文区 | `MessageTimeline.vue` L73-89 的 `toolCards/streamingText` 渲染，只留 pill |
| D3 | 删除渲染用累积串 | `agentStore.streamingText/streamingReasoning/toolCards` 渲染用途删除（遥测保留） |
| D4 | 删除兼容镜像 | `workspaceStore.syncMirror` + `sessionMessages` 渲染用途删除 |
| D5 | 写死 flag | `zcode-turn-units / zcode-markdown-stream / zcode-renderer-registry` 读取删除，按新值写死 |
| D6 | 收敛复制口径 | `toolCallRowToCard.mergeTurnText` 删除，统一到 `registry.mergeTurnTextFromBlocks` |

## 4. 分期与准出（每阶段独立提交、独立回滚）

| 阶段 | 内容 | 详见 | 准出 |
|---|---|---|---|
| P0 基线 | 探针 + 基线数据 + 顺序单测 | 05 §1 | 三档基线 + `vitest` 绿 |
| P1 单轨化 | 单分支 + pill + 数据源切换 | 05 §2 | 双协议各一轮、推理只出现一次、复制逐字一致 |
| P2 行表增量 | Map 行表 + 增量 `buildTurnUnits` | 05 §3 | 单帧 <5ms，重渲染 ≈ 末 turn 块数 |
| P3 渲染管线 | markdown 两档 + 懒挂载 + 滚动门控 | 05 §4 | 3k 字回答无 >200ms 长任务 |
| P4 清理锁死 | 删死代码 + 文档 + 门禁 | 05 §5 | `vue-tsc -b` + 全量回归绿 |

## 5. 文档索引

```text
00-README（本文）            ← 目标 / 破坏清单 / 分期准出
├─ 01-diagnosis-evidence.md  双轨证据 + 累积串证据 + 性能热点（行号锚点）
├─ 02-target-architecture.md 目标数据流 + 模块职责 + 状态归属 + 顺序/键/testid 契约
├─ 03-render-pipeline.md     markdown两档 + 懒挂载 + 滚动 + 工具卡（含截断值）
├─ 04-store-surgery.md       三 store 手术 + 事件批处理（diff 级）
├─ 05-implementation-plan.md P0~P4 施工清单（照抄执行）+ 提交切分
├─ 06-acceptance.md          验收矩阵 + 性能预算 + 验证命令
└─ 07-risks-rollback.md     风险表 + 分阶段回滚 + 工作量
```

## 6. 硬约束（施工期有效）

1. 后端落盘格式（`messages.jsonl`）、投影行协议字段只增不改；C# 仅允许事件节流类改动。
2. 不新增 npm 依赖；`target: chrome109` 不变；`naive-ui / vue 3.5 / pinia 2` 不变。
3. `data-testid / data-row-id / data-kind / data-status / data-gkey` 选择器一个不改；折叠默认态不变（reasoning 收起、toolGroup 收起、exec/fileEdit 展开规则不变）。
4. 右键复制/重放/分支/删除走新口径，文本与旧口径逐字一致（`mergeTurnTextFromBlocks` 为准）。
5. 每阶段结束 `npx vue-tsc -b` + `vitest run stores/__tests__ utils/__tests__` 全绿才能进下一阶段。
