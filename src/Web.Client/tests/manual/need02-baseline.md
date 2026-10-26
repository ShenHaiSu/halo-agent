# need02-P0 · 基线录制表（手工）

> 来源：`docs/need-02/04-P0-基线与总开关.md §基线录制`。
> 目标：P1–P4 动工前把“当前行为”钉死。P0 本身**零行为变更**——开关全 `off`
>（`data-need02="p1:off,p2:off,p3:off,p4:off"`）时必须与本基线逐像素一致。

## 0. 开关准备

```js
// 全关（= 基线行为）：逐个设 off
localStorage.setItem('halo-need02-p1', 'off');
localStorage.setItem('halo-need02-p2', 'off');
localStorage.setItem('halo-need02-p3', 'off');
localStorage.setItem('halo-need02-p4', 'off');
// 回默认（全开）：['halo-need02-p1','halo-need02-p2','halo-need02-p3','halo-need02-p4'].forEach(k => localStorage.removeItem(k))
```

验证点：时间轴根节点（`data-testid="message-timeline"`）应带有
`data-need02="p1:off,p2:off,p3:off,p4:off"`（全开时为 `p1:on,p2:on,p3:on,p4:on`）。

## 1. 基线场景

| # | 场景 | 步骤 | 留存物 | 基线结果（填写） |
| :--- | :--- | :--- | :--- | :--- |
| B1 | 普通问答（1 user + 1 正文） | 新会话 → 发送一句话 → 等终态 | Performance profile + 截图 + `sessionMessages.json` | _待录_ |
| B2 | 8 工具无工具正文轮 | 触发多工具任务（无正文）→ 等终态 | 同上 + `timelineItems` 快照 | _待录_ |
| B3 | 3 iteration 混合（正文+工具+失败1） | 触发混合任务 → 等终态 | 同上（P2 对比母版） | _待录_ |
| B4 | 图片 2 张 + 超限 1 张 | 发送含图消息（含超限图） | 缩略图时序录屏 | _待录_ |
| B5 | 切会话/重载/compact 各 1 次 | 流中切走切回；重载页面；触发 compact | veil/跟随行为录屏 | _待录_ |

## 2. P0 验收门

- [ ] 开关全 `on` / 全 `off` 各跑一次既有全量测试（`pnpm test`），零回退。
- [ ] `data-need02` 全 `off` 时，B1–B5 与本基线截图逐像素一致。
- [ ] `need02.spec.ts`（开关矩阵）全绿。

## 3. 回滚（P0）

- 运行期：四个 `localStorage` 键全部 `removeItem` 即回默认全开。
- 代码级：删除 `src/features/need02/utils/` 下 3 个新增文件
 （`need02Flags.ts` / `need02Debug.ts` / `need02.spec.ts`），
  并撤销 `useTimeline.ts` 的开关透传行、`TimelineView.vue` 的 `need02Attr`
  prop + `data-need02` 绑定、`MessageTimeline.vue` 的 `:need02-attr` 透传。

---

# need02-P2 · 统一轮次折叠终态验收记录（第三阶段）

> 来源：`docs/need-02/04-P2-统一轮次折叠终态.md §验收门` + `02 §2.1 G2` + `06 §6.3`。
> 决策口径：D1=B（上折叠行 + 下末段直出）、0 字隐藏、后端 B1–B3 暂不做（纯前端启发式）。

## 1. 自动化（已执行，2026-10-26）

| 层 | 命令 | 结果 |
| :--- | :--- | :--- |
| 纯函数 | `npx vitest run src/features/session/utils/userTurn.spec.ts` | 10/10 通过（单步不折/多步折/失败计数/字数口径/key 稳定/孤儿轮/pending 轮/system 不断轮/重试另起轮/空轮） |
| 组件 | `npx vitest run src/features/agent/components/userTurnBlock.spec.ts` | 6/6 通过（混合终态 1 行+末段直出/单步直出/pending 态/失败态/失败≥3/分节） |
| 回归 | `npx vitest run`（全量） | 41 文件 / 316 测试全绿（含 `need23-toolHandoff` / `need23-reloadDebounce` / `timelineVisual` / `m3-transitions` / `f05-timeline`，零回退） |
| 类型 | `npx vue-tsc --noEmit` | 干净 |
| 组件装配 | `npm run lint:components` + `npm run selfcheck` | 通过 |

## 2. 手工场景表（按 `06 §6.3`，需真机复核）

| # | 场景 | 步骤 | 期望 |
| :--- | :--- | :--- | :--- |
| P2-1 | 多步折叠 | 触发 3 iteration 混合（2 正文 + 5 工具 + 失败 1）→ 等 TurnCompleted | 终态 1 个 `user-turn-block`：上折叠行含 `过程内容`/`工具 5`/`失败 1`，下方末段正文全文直出；点开展示 2 节（非末段），不重复末段 |
| P2-2 | 单步回归 | 普通问答（1 user + 1 正文，无工具） | 无汇总行，正文直接渲染，与 P0 基线 B1 逐像素一致 |
| P2-3 | 失败≥3 | 触发 3 个 Tool Error | 折叠行红点 `×3` + 状态词 `失败` |
| P1 回看 | pending 态 |（P1 已验）发送中/失败态仍经 UserTurnBlock 头部展示 | `pending-sending` / `pending-failed` + 重发按钮 |
| G6 | 开关矩阵 | `localStorage.setItem('halo-need02-p2','off')` 后重载 | 回 need-01 行为（turn-run/turn 舱），`user-turn-block` 数量为 0 |

## 3. 回滚（P2）

- 运行期：`localStorage.setItem('halo-need02-p2','off')` 即回 need-01（P1/P3/P4 开关不受影响）。
- 代码级：revert 本阶段文件清单——新增 `userTurn.ts` / `userTurn.spec.ts` / `UserTurnBlock.vue` / `UserTurnSummary.vue` / `userTurnBlock.spec.ts`，及 `turnGroup.ts`（TimelineEntry 扩展）、`model.ts`（runId）、`useAgentTelemetry.ts`（activeUserTurnEntries）、`turnEvents.ts`（延迟清容器）、`useTimeline.ts`（分支+指纹复用+liveInclusiveSegs）、`TimelineRow.vue` / `TimelineView.vue` / `MessageTimeline.vue`（渲染链）、`historyToc.ts` / `HistoryTocPanel.vue`（目录按轮）、`workspaceStore.ts`（别名）、`shared/utils/turnGroup.ts`（barrel）。

## 4. P2 补验（overlay 贯通，2026-10-26）

- 新增 `src/features/agent/composables/userTurnOverlay.spec.ts`：仅设置 `turn.pendingUser`
  （不 mock 任何后端回包），断言 `useTimeline().timelineItems` 立即可见
  `user-turn`（`pending:true`，`key=uturn-pending-req1`）——锁定 G1“发送即现”
  不依赖回包/首字/防抖。注意：P2 投影源为 `pendingOverlayMessages`
 （含乐观 pending），而非 `model.persistedMessages`（裸 jsonl 投影）。
