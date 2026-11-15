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

---

# need02-P3 · 平滑过渡动画验收记录（交接编排）

> 来源：`docs/need-02/04-P3-平滑过渡动画.md` + `05 R6/R11` + `06 §6.3 P3-1/P3-2`。
> 结论：P2 硬切之上加 300ms 交接窗（`model.liveCompleting`，reloadNow 之前即立），
> 内嵌 LiveBody 与终态经 `UserTurnTransition.vue` 交叉淡入淡出（仅 opacity/translate，高度零动画）。

## 1. 自动化（已执行，2026-10-26）

| 层 | 命令 | 结果 |
| :--- | :--- | :--- |
| 组件 | `npx vitest run src/features/agent/components/userTurnTransition.spec.ts` | 5/5 通过（运行态共存/completing 共存+淡入淡出类/稳态只留终态/p3 off 硬切/data-handoff） |
| 时序 | `need23-reloadDebounce.spec.ts` P4 分组 | 交接窗内 segs 保留 + `liveCompleting=true`，300ms 后清容器并落窗；窗内起新轮旧定时器拦截 |
| 回归 | `npx vitest run`（全量） | 47 文件 / 352 测试全绿（含 `m3-transitions` / `timelineVisual`，零回退） |
| 类型 | `npx vue-tsc --noEmit` | 干净 |
| 自检 | `npm run lint:components` + `npm run selfcheck` | 通过 |

## 2. 手工场景表（按 `06 §6.3`，需真机复核）

| # | 场景 | 步骤 | 期望 |
| :--- | :--- | :--- | :--- |
| P3-1 | 轮末交接 | 停底部观察 TurnCompleted | 无消失帧（live 淡出 + 终态淡入 0.14s），pinned 位移 ≤4px |
| P3-2 | 上翻阅读 | 流中上翻 | 跟随解除 + 回底 pill，交接动画暂停（直接切终态，05 R11） |
| G6 | p3 开关 | `localStorage.setItem('halo-need02-p3','off')` 后重载 | 回 P2 硬切（交接窗内直接切终态，live 不共存），内容不断 |

## 3. 回滚（P3）

- 运行期：`localStorage.setItem('halo-need02-p3','off')` 即硬切（`handoffAnim=false` 穿透 TimelineView→TimelineRow→UserTurnBlock→UserTurnTransition）。
- 代码级：删除 `UserTurnTransition.vue` + `userTurnTransition.spec.ts`，`UserTurnBlock` 回 v-if/v-else 链，
  `model.ts` 删 `liveCompleting`，`turnEvents.ts` 回 P2 defer（无 completing 标记）。

---

# need02-P4 · 架构与交互优化验收记录（第四阶段）

> 来源：`docs/need-02/04-P4-架构与交互优化.md` + `06 §6.3 P4-1/P4-2/G6`。
> 开关：`halo-need02-p4`（master）+ `halo-need02-p4-proj/md/reload/window`（子项，各自默认 on，可逐个 off）。

## 1. 交付清单（实现 vs 文档）

| # | 文档项 | 实现 | 说明 |
| :--- | :--- | :--- | :--- |
| P4a-1 | `buildUserTurnsIncremental` | `userTurn.ts` + `userTurnIncremental.spec.ts`（10） | 指纹=条数+首尾id+内容长度和+opts；命中返同一引用；追加只重建末轮；`useTimeline`/`useAgentTelemetry` 双源接入（各持 cache） |
| P4a-2 | `renderMarkdown` 缓存 LRU200 | `markdownRender.ts` + `markdownCache.spec.ts`（4） | 键=`blockHash`（复用 `turnGroup.ts`）；`MessageTimeline.renderHtml` 按 `p4-md` 即时切换 |
| P4a-3 | `toCard/failedCount` 派生进 Step | 等效实现：`useTimeline.toCard` 记忆化（callId+内容指纹，有界300，按 `p4-proj` 开关） | 未搬 schema——`UserTurnStep` 被多处消费，搬字段属 breaking；记忆化达同等“一次算好、模板零 find”效果 |
| P4a-4 | 重拉收敛（persisted 只刷预算，轮末全量） | `turnEvents.ts scheduleBudgetOnly` + `need23-reloadDebounce` 新分组（4） | persisted→预算（400ms 防抖保留）；全量只在 finish（reloadNow）；8 工具轮全量 `getSessionMessages` 由 N 次→≤2（首乐观+轮末） |
| P4a-5 | 窗口按高度加权 | `transcriptWindow.ts turnWeightOf/hiddenAboveByWeight` + `transcriptWindowWeight.spec.ts`（7），`useTimeline` 挂载/扩窗双接入 | 权重=1+steps，预算 32（≈15 平均轮）；超高轮整轮挂载不截断。轮内“只挂 user 头+汇总行”未做（默认收起已等效首屏语义），列 follow-up |
| P4a-6 | 缩略图合批 + lost 缓存 | `useTimeline` idle 合批（每批≤6，`requestIdleCallback`+timeout 300 兜底） | 无子开关，跟 master p4；`lost` 语义不变 |
| P4b-1 | 输入框发送中态 | `PromptComposerView` `composer-sending`（`发送中… Esc 停止`）+ 停止按钮既有高亮 | 输入框不置灰（草稿可继续编辑，有据决策，见本记录 §4） |
| P4b-2 | 失败重试带图 | P1 已交付（`retryLastTurn` 走 `lastAttachments`），本阶段不断言回归（全量绿） | — |
| P4b-3 | 汇总行信息密度 | D1=B 生效：折叠行=状态图标+`过程内容·耗时·工具·失败`+红点；字数不进折叠行（用户 2026-10-26 定稿，`fullSummary` 供展开区/测试） | 与 `04-P4` 表格字数项冲突处以用户决策为准 |
| P4b-4 | Toc 按轮 | P2 已交付（`HistoryTocPanel` 消费 `activeUserTurnEntries`，`historyToc` 按轮一条）；本阶段 Toc 源切增量投影 | — |
| P4b-5 | 右键菜单按轮 | `userTurn.ts turnMessageIds/turnCopyText/turnReplayAnchor` + `useTimelineMenu.openTurnMenu` + `MessageTimeline` 轮级分流 | 复制整轮全文；重放/分支走轮锚点；删除整轮（二次确认一次，逐条删，复用 `deleteMessage`） |
| P4b-6 | 空态/错误态收轮内 | `noticeInTurn`（p4&&p2）：末轮 `UserTurnBlock` 底部（`user-turn-notice`）/ 非 user-turn 末条行尾兜底；全局 alert 互斥隐藏 | 内联非悬浮；`retry/dismiss` 事件复用 |

## 2. 自动化（已执行，2026-10-26）

| 层 | 命令 | 结果 |
| :--- | :--- | :--- |
| 纯函数 | 新增 4 文件（`userTurnIncremental` 10 / `markdownCache` 4 / `transcriptWindowWeight` 7 / `need02-p4` 5）+ `need23-reloadDebounce` +4（含竞态守卫） | 全绿 |
| 组件 | `userTurnTransition` 5 / `userTurnBlock` 6 / overlay / pending render | 全绿 |
| 回归（全开） | `npx vitest run` | 47 文件 / 352 测试全绿 |
| 回归（全关） | 临时 setup（p1–p4+四子项全 off）跑全量 | 47 文件 / 352 测试全绿（G6 可回滚实证；临时配置已删，不进版本） |
| 类型 | `npx vue-tsc --noEmit` | 干净 |
| 自检 | `npm run lint:components` + `npm run selfcheck` | 通过 |
| eslint | 变更文件 | 0 新增 error（2 处 error 为基线已存在：`model.ts:7`、`TimelineRow.vue void isPendingMsg`，与本次无关） |

## 3. 手工场景表（按 `06 §6.3`，除 G6 外需真机复核）

| # | 场景 | 步骤 | 期望 | 状态 |
| :--- | :--- | :--- | :--- | :--- |
| P4-1 | 8 工具性能 | 连续 8 工具 | FPS≥50，network 全量拉 ≤2 | 待真机（代码证据：persisted 零全量 + 轮末 1 次 + md/toCard 双缓存） |
| P4-2 | Toc/菜单 | 点击目录/右键 | 按轮定位 + 整轮复制/重放/分支/删除 | 待真机（单测锁死纯函数 + 事件链复用旧链） |
| P1-1~P2-3 | P1/P2 回看 | 见 P1/P2 记录 | 行为不变 | 自动化覆盖（overlay/block 单测全绿） |
| G6 | 开关矩阵 | p1–p4 及 `p4-proj/md/reload/window` 逐个 off | 各回旧行为（见 §4） | 自动化实证（全关全绿）+ 需真机目视 |

## 4. 决策与偏差记录（评审用）

1. **P4b#1 输入框不置灰**：`handleSend` 在生成中直接返回，草稿保留可编辑；置灰会阻断“边跑边改下一问”。
   交付为状态提示行 + 停止按钮高亮（既有），满足“发送中态/字数预期”验收口径。
2. **P4a#3 等效实现**：记忆化代替 schema 搬迁（见 §1 表格），避免 breaking `UserTurnStep` 的 4 处消费者。
3. **P4b#3 字数口径**：折叠行无字数（D1=B 用户定稿）；`04-P4` 表格中的“正文x字/思考x字”仅 `fullSummary`（展开区/测试）保留。
4. **P4a#5 部分实现**：权重预算 + 整轮挂载已交付；超高轮“只挂 user 头+汇总行”（轮内裁剪）列 follow-up，
   默认收起态下首屏语义已等效（汇总行恒可见，明细折叠）。
5. **P3 先于 P4 补齐**：P3（交接编排）在基线上缺失（仅 P2 的 300ms defer，无交叉淡入淡出与 p3 开关），
   本次一并交付（见上节 P3 记录），否则 G6 的 `halo-need02-p3` 无行为锚点。
6. **defer 竞态守卫**：交接窗内起新轮时旧定时器跳过清空（`turnStartMarks` 比对，单测锁定）——P2 遗留竞态，本次修复。

## 5. 回滚（P4）

```js
localStorage.setItem('halo-need02-p4','off'); // 全回 P2 行为（性能/交互回旧，折叠语义保持）
localStorage.setItem('halo-need02-p4-proj','off'); // 增量投影→全量；toCard→直算
localStorage.setItem('halo-need02-p4-md','off'); // md 缓存→直跑（即时生效，无需重载）
localStorage.setItem('halo-need02-p4-reload','off'); // persisted→全量重拉旧链
localStorage.setItem('halo-need02-p4-window','off'); // 窗口→条数旧链
// 全删即回默认全 on
```

- 代码级：按子项 revert（各有独立 commit 边界，见文件头 `need02-P4` 注释）；死代码删除（D5=A：
  `turnGroup.ts buildTurnGroups / turnRun.ts / TurnRunItem.vue / TurnGroupItem.vue`）**本次未执行**，
  留待 P4 行为稳定一个版本后单独一 commit（见 `07` 风险 TOP2：外部目录/导出依赖需先确认）。

