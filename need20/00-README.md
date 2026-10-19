# need20 右侧边栏修复：浮卡移除 + 抽屉空内容修复

> 状态：修复方案（待施工）。
> 前置：`docs/need19/00~08`（施工蓝图）+ 已落盘的 need19 实现（P0~P3）。
> 作用域：`develop/src/Web.Client` 纯前端，后端零改动。
> 一句话目标：① 彻底移除外部浮动 Goal 卡（抛弃浮动设计）；② 修复 Drawer 打开后"只有横线 + X" 的空内容问题，恢复"左 48px 纵向 icon rail + 右内容区"两段式布局。

---

## 1. 用户验收反馈（原文 → 缺陷编号）

| # | 用户反馈 | 缺陷编号 | 严重度 |
|---|---|---|---|
| 1 | 目标列表没有收纳到右侧边栏，而是依然在外部表现为一个浮动按钮；该浮动按钮设计可以抛弃 | F1 浮卡未移除 | 高（与需求终态不符） |
| 2 | 点开右侧边栏后只有上方一条横线和一个关闭按钮，再无其他；不符合"左边纵向 tab（正方形 icon）+ 右侧内容区" | F2 抽屉空内容 | 致命（功能不可用） |

---

## 2. 根因一句话

| 缺陷 | 根因一句话 | 证据主锚点 |
|---|---|---|
| F1 | need19-02 §4"并存策略"被当成硬约束执行，`WorkbenchView` 仍挂载 `GoalFloatPanel`，浮卡三态 UI 完整保留 | `views/WorkbenchView.vue:7`、`components/goal/GoalFloatPanel.vue:1-19` |
| F2 | `RightRailDrawer` 用 `absolute inset-0` 零尺寸 div 包裹 + `:to` 指回同一容器内，自指 Teleport；`n-drawer-content` body 无显式高度，`right-rail-root{height:100%}` 相对高度为 0 的父级被压扁，只剩 header 横线 + X | `components/rightRail/RightRailDrawer.vue:2,10,80,108-114`、`node_modules/naive-ui/es/drawer/src/Drawer.mjs:273` |

详细证据链见 [01](./01-diagnosis-evidence.md)。

---

## 3. 修复范围（最小侵入）

| 类型 | 文件 | 动作 |
|---|---|---|
| 删除 | `src/components/goal/GoalFloatPanel.vue` | 整文件删除（浮动设计抛弃） |
| 修改 | `src/views/WorkbenchView.vue` | 删除第 7 行 `<GoalFloatPanel />` 挂载 + 删除 import |
| 修改 | `src/components/rightRail/RightRailDrawer.vue` | 去掉 `.right-rail-host` 包裹层；`to` 回退 `'body'`；根高度兜底；删除"打开 goal tab 收 mini 浮卡"的 watch 分支 |
| 修改 | `src/components/rightRail/RightRailTrigger.vue` | 不变（保留，仍是唯一入口）；如需可微调 tooltip 文案 |
| 保留 | `src/components/goal/GoalPanelContent.vue` | 不变，Drawer 内唯一 Goal 实现 |
| 保留 | `src/stores/goalStore.ts` | 不变（`watchSession` 已在抽屉内，幂等守卫保留） |
| 新增验证 | `scripts/toc.selfcheck.mjs` | 不变（纯函数不受影响） |

> 不动：`App.vue / bridge / workspaceStore / subSessionStore / agentStore / MessageTimeline 锚点逻辑 / SubHistoryView 锚点逻辑 / turnGroup / historyToc / useTimelineNav / uiStore / main.css`（除非验收发现 rail 仍需补高度，见 02 §4）。

---

## 4. 修复后期望形态

```text
WorkbenchView
├─ MessageTimeline / SubHistoryView / PromptComposer / AskUserModal
├─ RightRailTrigger（右侧中部悬浮，唯一入口）
└─ RightRailDrawer（to="body"，overlay 整窗右侧）
     ├─ nav.right-rail-tabs（48px 纵栏，两个正方形 rail-tab）
     └─ section.right-rail-content（GoalPanelContent | HistoryTocPanel）
```

- Drawer 关闭：主界面无任何浮动卡，与 need19 之前"像素一致"的要求自然解除（用户已明确抛弃浮卡）。
- Drawer 打开：左侧 48px 纵向 icon rail（🎯/☰，36×36 正方形按钮），右侧内容区按 Tab 切换。

---

## 5. 文档索引

```text
00-README（本文）            ← 缺陷总览 / 根因一句话 / 修复范围 / 期望形态
├─ 01-diagnosis-evidence.md  逐行代码证据 + 自指 Teleport 推演 + 对照组反证
├─ 02-fix-plan.md            分步施工清单（diff 级，可照抄执行）+ 风险与回滚
└─ 03-acceptance.md          验收矩阵（F1/F2 必过 + 回归红线）+ 验证命令
```

---

## 6. 硬约束（修复期有效）

1. 后端零改动；不新增依赖；`target: chrome109` 不变。
2. `GoalPanelContent` 为 Goal 唯一实现，不复制第二套。
3. `TimelineNav / buildTimelineEntries / historyToc` 已验证链路不动。
4. 删除 `GoalFloatPanel` 后，全仓不得残留对其的 import（`vue-tsc` 会卡）。
5. Drawer 仍满足 need19 R6/R7：`:mask-closable="false"` + 透明遮罩 + 右上角 X 可关。
