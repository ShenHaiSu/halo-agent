# 06 诊断中心「智能体行为」重设计（IA × 布局 × 规范）

> 范围：只动 `agent` Tab。触点：`components/settings/AgentBehaviorView.vue` 拆小（壳+5 卡）、`components/settings/SettingsCenterDrawer.vue`（微调）、`stores/agentConfigStore.ts`（不动字段，加 Goal 摘要只读）、`bridge/hostBridge.ts`（Goal 类型复用 03）。

## 1. 现状痛点（逐条对 `AgentBehaviorView.vue:1-365`）

| # | 痛点 | 位置 |
|---|---|---|
| P1 | 695 行单文件，A–E 五卡纵向堆叠，改一处抖全局 | 全文件 |
| P2 | 说明+保存混在 A 卡，脏态提示易被淹没（`dirty` 行在卡内） | L11-46 |
| P3 | B/C/D 三配置卡控件三件套（输入+预设+恢复默认）对齐不一，`pl-[104px]` 硬编码缩进 | L78-86 等 |
| P4 | D 子代理开关/数字/权限三组平铺，危险项（开 exec/file）无视觉区分 | L218-341 |
| P5 | E 诊断表 `n-data-table` 无定宽列、无空态图、无自动刷新，拒绝信息只有一行 alert | L343-361 |
| P6 | 无 Goal/预算快照位，监控与配置割裂 | — |
| P7 | 1020px 宽下 `w-[280px]` 模型选择器等行内宽度在小窗溢出（TopBar 同源问题） | Drawer 1020/94vw |

## 2. 新 IA（三区 + 置顶操作条）

```text
┌ sticky action bar: ●脏态/○干净 | [保存][恢复全部默认] | fallbackReason横幅位 ┐
├ 区① 运行配置（2 卡并排，窄窗堆叠） ──────────────┤
│ [命令执行超时卡] [最大轮次卡]                      │
├ 区② 子代理与目标（2 卡并排） ────────────────────┤
│ [子代理配置卡：开关/配额/权限] [目标摘要卡：只读+去浮动窗编辑] │
├ 区③ 实时监控与诊断（全宽） ──────────────────────┤
│ [子运行监控卡：运行表+自动刷新+行内历史入口+掐断]      │
│ [拒绝与自愈记录卡：最近 N 条 code/callId/时间]        │
└──────────────────────────────────────────────────┘
```

- A 说明卡折叠为 action bar 下一行 `n-alert info` 可收起（默认收起，`localStorage` 记）。
- 锚点导航：区标题右侧小字 `超时 · 轮次 · 子代理 · 目标 · 监控 · 拒绝` 跳转（`scrollIntoView` 复用既有 `scrollToField` 模式）。

## 3. 组件拆分

```text
components/settings/agent/
  AgentBehaviorView.vue        // 壳：action bar + 三区 grid + 锚点（<150 行）
  cards/AgentTimeoutCard.vue   // B（默认/上限两行）
  cards/AgentIterationsCard.vue // C
  cards/SubAgentConfigCard.vue  // D（开关/配额/权限三组）
  cards/SubRuntimeMonitorCard.vue // E 升级（表+轮询+历史入口+掐断）
  cards/GoalSummaryCard.vue     // 新（只读摘要+ [去浮动窗编辑] 跳主界面展开浮动窗）
  cards/RejectionLogCard.vue    // 新（LastRejection 扩展为近 10 条内存环 buffer，后端加）
```

- store：`agentConfigStore` 字段零增；新增 `goalSummary`（调 `getGoal` 只读）与 `rejections[]`（`SubRuntimeInfo.LastRejection` 单条→后端环 buffer `RecentRejections[10]`，`SubAgent.cs` 加字段，老前端忽略未知字段兼容）。
- 后端小改（可选但推荐）：`SubRuntimeInfo` 加 `RecentRejections: List<SubRejectionInfo>`（Coordinator 内 `ConcurrentQueue` 记最近拒绝，`SaveAgentConfig` 校验拒、spawn 超限拒、子写 skill/memory/goal 拒全记）。

## 4. 布局与样式规范（规整重点）

- 网格：`grid grid-cols-1 xl:grid-cols-2 gap-3`（区①②卡并排，`xl` 以下堆叠）；区③全宽 `xl:col-span-2`。
- 卡片：统一 `n-card size=small` + 头部 `title + 右侧操作（恢复本项默认）`；内容行统一 `label w-24 + control flex-1 + suffix`，删除 `pl-[104px]` 硬编码，改 `grid grid-cols-[96px_1fr_auto]`。
- 数字输入统一三件套：`n-input-number（w-44+suffix）+ 预设组 + 恢复默认` 同行；`dmInvalid/overLimit` 时整卡红框 + 保存禁用（既有逻辑保留，见 `AgentBehaviorView.vue:422-432`）。
- 危险开关：`exec/file 开 → amber 描边；memory/skill 开 → 红色描边 + tooltip 警告`（既有 `sub-perm-danger` 类保留并推广）。
- 表格：列定宽（状态 90/标签 140/耗时 100/工具 140/操作 120），`ellipsis` + 空态 `n-empty "当前无子代理运行"` + 5s 自动刷新（切 Tab 即停）+ 手动刷新按钮保留。
- 颜色 token：沿用 `text-primary/secondary、bg-surface/elevated、border-subtle`；状态色复用 TopBar 三段；暗色下表格 hover 用 `hover:bg-elevated/60`（与 ToolCallCard 一致）。
- Drawer 微调：`width:1020` 保留；左 Tab 116px 保留；内容区 `padding 8px` 保留；仅要求三区在 94vw 下无横向滚动（表格容器 `overflow-x:auto` 内收）。

## 5. data-testid（老 ID 全保留 + 新增）

保留：`agent-save/agent-reset-all/agent-timeout-default-input/agent-sub-*`（见既有全套）。
新增：`agent-action-bar / agent-goal-summary / agent-goal-edit-jump / agent-runtime-table / agent-rejection-row / agent-section-nav`。

## 6. 施工顺序（本篇内）

1. 先抽 `AgentTimeoutCard + AgentIterationsCard`（纯搬运，行为零变，老单测先过）。
2. 再 `SubAgentConfigCard`（D 卡搬运 + 危险描边）。
3. 再 `SubRuntimeMonitorCard + RejectionLogCard`（E 卡升级 + 后端环 buffer）。
4. 最后 `GoalSummaryCard`（依赖 02/03 联调通过）+ 壳组装 + 锚点。
5. 每步 `vue-tsc -b && vite build` + 1020px/暗色截图自查。

## 7. 不做

其它 8 个 Tab、Drawer 换框架、图表化用量（用量分析 Tab 已有）、移动端。
