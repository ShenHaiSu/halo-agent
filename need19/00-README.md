# need19 总览：主界面右侧边栏（Goal 面板 + 会话历史快速跳转目录）

> 状态：施工方案（待实现）。
> 作用域：`develop/src/Web.Client`（纯前端 + 1 处 store 扩展），**后端零改动**。
> 一句话目标：在 Workbench 主界面右侧新增一个可收纳的 **侧边 Drawer**，采用「左竖排 icon Tab + 右内容区」两段式布局，承载两个面板：
> ① **Goal 面板**（接管现有浮动 Goal 卡片，支持编辑/重置）；
> ② **快速目录跳转**（把当前会话历史渲染成左右对齐的聊天气泡，每泡显示消息正文前 15 字，点击精确滚动到主界面锚点并高亮）。
> Drawer 打开后：**点击外部（遮罩）不关闭**，只能点右上角关闭按钮（或再次点触发按钮 / Esc）收起。

---

## 1. 需求拆解（用户原话 → 可验收契约）

| # | 用户诉求 | 可验收契约（Definition of Done） |
|---|---|---|
| R1 | 主界面添加右侧边栏 | Workbench 右侧出现一个 Drawer；不占用主时间轴的常驻宽度（打开才占位/覆盖）；关闭后主界面与今日像素级一致 |
| R2 | 承载 Goal 面板 | Goal 的全部信息（标题/描述/进度/里程碑/备注/损坏告警）在 Drawer 内可见可编辑可重置；与主界面浮动卡数据同源（`goalStore` 单例） |
| R3 | 承载历史快速跳转目录 | 当前会话每一条「可见消息」对应一个气泡；user 右对齐、assistant 左对齐；气泡文本 = 正文前 15 字符；点击后主界面滚动到该消息锚点并短暂高亮 |
| R4 | 左 icon Tab + 右内容区 | Drawer 内部左侧 44~48px 竖排 icon 栏（仅 icon，含 tooltip + 计数徽标），右侧为内容区 |
| R5 | 用 Drawer 实现 | 使用 Naive UI `n-drawer` + `n-drawer-content`，`placement="right"` |
| R6 | 点击外部不收缩 | 点遮罩无任何反应（不关闭、不闪、不改变状态） |
| R7 | 右上角关闭按钮才收缩 | `n-drawer-content` 自带 `closable` 关闭按钮（同款 X 图标）必须可用；同时保留触发按钮二次点击与 Esc 作为辅助路径 |
| R8 | 形成左右聊天气泡 | 气泡有明确的左右区分语义色 + 对齐 + 圆角方向（与主时间轴视觉同源） |
| R9 | 每泡对应每条消息正文前 15 字 | 纯函数 `bubbleLabel()`：去 Markdown/图片占位/换行/多余空白 → 15 个「字」（`Array.from` 码点，中文/emoji 安全）→ 超长加 `…`；空正文给角色兜底文案 |

## 2. 现状证据地图（代码锚点，2026-10-19 实测）

| 关注点 | 现状 | 代码锚点 | 对本期的影响 |
|---|---|---|---|
| 主界面布局 | `App.vue`：`n-split`（左工作区侧栏 / 右工作台）+ 底部 26px 状态栏；右工作台 = `WorkbenchTopBar` + `WorkbenchView` | `src/App.vue:13-37`、`:128-137` | Drawer 以「组件方式」挂在 `WorkbenchView` 内，天然被 `n-split` 裁剪，只遮住右工作台 |
| 右工作台内容 | `WorkbenchView` = `MessageTimeline` + `PromptComposer` + `GoalFloatPanel` + `AskUserModal`，根节点 `relative overflow-hidden` | `src/views/WorkbenchView.vue:2-9` | Drawer 内的 `n-drawer` 若 `to` 未指定会 Teleport 到 `body` → 会盖住左侧工作区侧栏，需要显式 `to`（见 [04](./04-drawer-spec-and-behavior.md) §3） |
| 现有 Drawer 范式 | `SettingsCenterDrawer` 用 `n-drawer placement="right" width=1020` + `n-tabs placement="left" type="card"` | `src/components/settings/SettingsCenterDrawer.vue:2-14` | Tab 范式可参考，但卡片式 Tabs 与「only icon」不符 → 自绘 icon rail |
| 消息真相源 | `workspaceStore.sessionMessages: UnifiedChatMessage[]`，切换会话 `loadSessionMessages()` 整表替换；追加走 `push`；轮末 `loadSessionMessages` 对账 | `src/stores/workspaceStore.ts:24,342-362,364-391` | 目录气泡**必须**由同一数组派生（computed），不允许自己缓存一份 |
| 时间轴聚合 | `buildTurnGroups()` 把 assistant+tool* 归并成 turn；`MessageTimeline` 再按 jsonl 顺序展开成 `timelineItems`（user/system/turn） | `src/utils/turnGroup.ts:41-84`、`src/components/chat/MessageTimeline.vue:192-225` | 目录条目必须复用 **同一套** `buildTimelineItems` → 气泡与主界面条目一一对应，不会出现「目录有、界面没有」 |
| 现有锚点工具 | `historyAnchor.ts` 只提供角色判定 + 滚动尾展开 + 剪贴板双通道，**没有**「消息 → DOM」定位 | `src/utils/historyAnchor.ts:17-32` | 需新增锚点注册表 + 滚动纯函数（[03](./03-history-anchor-and-scroll.md)） |
| 滚动容器 | `MessageTimeline` 内部 `scrollContainerRef` 私有不外露；已有「进入会话/新增条目自动滚到底」 | `src/components/chat/MessageTimeline.vue:2,459-503` | 需抽 `useTimelineScroll` 并用 `provide` 暴露 `scrollToAnchor`（不破坏现有自动滚底） |
| 自动滚底冲突 | `timelineItems.length` 变化 → 自动滚到底 | 同上 `:492-497` | 跳转后到 300ms 内若有新条目会把视口拽回底部 → 必须「跳转锁 + 只滚到自身渲染」（[03](./03-history-anchor-and-scroll.md) §5） |
| Goal 数据 | `goalStore`（`goal/save/clear/load/panel/pos/updatedFlash`）+ 后端 `GetGoal/SaveGoal/ClearGoal`，事件 `goal:updated` | `src/stores/goalStore.ts:8-103`、`src/components/goal/GoalFloatPanel.vue:48-119`、`src/Host.Desktop/Bridge/HostBridgeFacade.Goal.cs:27-78` | 后端已够用；只需把 `GoalFloatPanel` 的编辑逻辑抽成共享面板组件 |
| Goal 生命周期 | `goalStore.watchSession()` 在 `GoalFloatPanel.onMounted` 里调用（含 `haloEventBus.on('goal:updated')`） | `GoalFloatPanel.vue:113-118`、`goalStore.ts:91-99` | 若删掉浮动卡就会丢监听 → 必须把 watcher 迁到 Drawer 根组件，保证「唯一挂载点」 |
| 子会话模式 | `subSessionStore.mode === 'sub'` 时主界面显示只读子历史；`SubSessionDetailDto.status` 可为 running/pending | `WorkbenchView.vue:3-6`、`src/stores/subSessionStore.ts:5-14,95-102` | 子会话没有 `goal.json`（后端统一归一到父）；目录需有「子会话目录」模式 |
| 设计变量 | 语义色 `--bg-* / --border-subtle / --text-* / --accent-primary`；字号 `--app-font-size`；`chat-timeline-centered/full` | `src/styles/main.css:5-42,104-129`、`tailwind.config.js:28-37` | 新组件一律走变量，禁止硬编码色值（亮/暗同源） |
| 浏览器基线 | Vite `target: chrome109`；CefSharp 109 内嵌；CSS 禁用 `:has()/container query/color-mix()` | `vite.config.ts:24-27`、`main.css:146` 注释 | 动画/布局只用 flex + var + transition |
| 可测试出口 | **全仓无前端单测框架**（`package.json` 仅 dev/build/preview） | `src/Web.Client/package.json` | 纯函数采用「双导出 + node 断言脚本」验收，不引入 Vitest（[06](./06-implementation-and-acceptance.md) §4） |
| Naive Drawer 行为（源码实证） | `maskClosable` 默认 `true`；遮罩点击 → `handleMaskClick` → `doUpdateShow(false)`；`closeOnEsc` 默认 `true` → Esc 关闭；**`n-drawer-content` 的 X 按钮始终调用 `doUpdateShow(false)`** | `naive-ui/es/drawer/src/Drawer.mjs:29-44,117-125,155-170` | `:mask-closable="false"` 即可满足 R6；X 按钮不受影响，天然满足 R7 |
| 主题层级 | `n-dialog-provider / n-notification-provider / n-message-provider` 都在 App 根部 | `src/App.vue:8-10` | Drawer 内可直接 `useMessage()/useDialog()`（与 `GoalFloatPanel` 一致） |
| Goal 零散写入点 | `AgentBehaviorView` 直接写 `goalStore.goal` | `src/components/settings/AgentBehaviorView.vue:96` | 本期不动它，但 Drawer 面板需「进入即 `load()`」，避免脏读 |

## 3. 方案一句话

**把「右侧边栏」做成 `WorkbenchView` 内的一个组件级 Drawer（挂载点 = 右工作台容器），内部自绘 icon rail + 内容区；两个面板共享 `goalStore` 与 `sessionMessages` 两个唯一真相源；历史面板复用 `buildTimelineItems()` 生成气泡，通过 `provide/inject` 的 `TimelineNav` 完成「气泡 → DOM 锚点」的精确滚动与高亮。**

```text
┌──────────── 应用窗口 ───────────────────────────────────────────────┐
│ n-split ┌ WorkspaceSessionSidebar ┬ WorkbenchTopBar ─────────────┐  │
│         │                         │  MessageTimeline          ▲ │  │
│         │                         │  PromptComposer           │ │  │
│         │                         │  GoalFloatPanel(关闭时显示)│ │  │
│         │                         │  ★ SideRailTrigger(⌘)     │ │  │
│         │                         │                           │ │  │
│         │                         │   ┌ n-drawer(right) ──────┴─┤  │
│         │                         │   │ ┌rail┬ content───────┐ │  │
│         │                         │   │ │ 🎯  │ Goal 面板     │ │  │
│         │                         │   │ │ ☰   │ 历史气泡目录  │ │  │
│         │                         │   │ └────┴───────────────┘ │  │
│         │                         │   └───────────────────────┘  │  │
│         └─────────────────────────┴──────────────────────────────┘  │
│ 26px StatusBar                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

## 4. 硬约束（全期有效，任何子文档不得违背）

1. **零后端改动**：不新增 RPC、不改 `goal.json`、不改事件名。需要的能力用现有 `getGoal/saveGoal/clearGoal`、`getSessionMessages`、`getSubSessions/getSubSessionMessages` 组合。
2. **单一真相源**：目录气泡只来自 `workspaceStore.sessionMessages`（+ 子会话 `subSessionStore.messages`）；Goal 只来自 `goalStore.goal`。禁止组件内另存一份派生缓存（可用 computed）。
3. **不改变主界面既有行为**：不动 `MessageTimeline` 的渲染结构、不删 `GoalFloatPanel` 的任何 `data-testid`（Drawer 关闭时用户仍看到浮动卡），自动滚底逻辑行为不变（仅替换实现，见 [03](./03-history-anchor-and-scroll.md) §5.4）。
4. **不改变主界面 x 轴尺寸**：采用「overlay 悬浮 Drawer」，不插入 flex 轨道；否则 `chat-timeline-centered` 会重排，触发 need04/need02 的排版回归。
5. **`to` 内联到 WorkbenchView**：Drawer 只覆盖右工作台（含顶栏），不遮左侧工作区侧栏，保证 `n-split` 可继续拖拽。
6. **遮罩「零反应」**：`:mask-closable="false"`；且不弹「请点右上角」的提示（不打扰），仅在首次打开时于内容区顶部给一次性引导文案。
7. **Win7/Chrome109 兼容**：只用 flex/transition/var；不使用 `:has()`、`container query`、`color-mix()`、`scrollIntoView({behavior:'smooth'})` 之外的实验特性；`content-visibility` 只做增强（不支持也不影响功能）。
8. **5 字/15 字口径统一**：会话标题 5 字沿用 `instantTitleFromUserText`（`sessionTitle.ts:6`），**气泡 15 字新增独立纯函数**，不复用 5 字函数的正则链（避免标题口径被污染）。
9. **无新增依赖**：不引入虚列表库；长会话用「分页渲染 + 窗口化」手写实现（[05](./05-quick-jump-panel.md) §4）。
10. **落盘文档纪律**：`docs/need19/` 为过程稿，落地验收后按 `docs/pitfalls/README.md` 规则提炼，不长期保留。

## 5. 文档索引与阅读顺序

```text
00-README（本文）        ← 需求契约 / 现状证据 / 硬约束 / 索引
  ├─ 01-architecture-design.md      布局与组件树、状态分布、数据流、与现有组件关系、视觉规格
  ├─ 02-goal-panel.md               Goal 面板：抽取共享面板、watchSession 迁移、浮卡与抽屉并存策略
  ├─ 03-history-anchor-and-scroll.md 锚点体系、跳转算法、自动滚底冲突、高亮、子会话目录
  ├─ 04-drawer-spec-and-behavior.md  Drawer 规格：mask 禁用关闭、Esc/焦点/滚动锁、宽度拖拽与持久化、Tab 规格
  ├─ 05-quick-jump-panel.md         快速目录面板：气泡 UI、标签纯函数、过滤/分组/窗口化、性能
  ├─ 06-implementation-and-acceptance.md 施工顺序（P0-P4）、文件清单、回归红线、验收矩阵、回滚
  ├─ 07-edge-cases.md               空态/长文本/流式中/搜不到锚点/压缩后失锚/切会话/存储异常等
  └─ 08-code-skeleton.md            可照抄的骨架代码（uiStore / TimelineNav / Timeline diff / RightRailDrawer）
```

**建议施工顺序**：04（Drawer 骨架，可独立预览）→ 03（锚点能力，纯函数 + Timeline 改造）→ 05（目录面板）→ 02（Goal 面板）→ 06（验收）。02/05 可并行；动笔前先看 [08](./08-code-skeleton.md) 定接口。

## 6. 改动文件总览（一句话版）

| 类型 | 文件 | 说明 |
|---|---|---|
| 新增 | `src/stores/uiStore.ts` | Drawer 开关/Tab/宽度/跳转锁/目录过滤（localStorage 持久化） |
| 新增 | `src/utils/historyToc.ts` | 目录纯函数（`toDisplayText`/`bubbleLabel`/`buildHistoryToc`） |
| 新增 | `src/composables/useTimelineNav.ts` | `TimelineNav` 类型 + `provideTimelineNav/useTimelineNav` + 锚点注册表 |
| 新增 | `src/composables/useTimelineScroll.ts` | 从 `MessageTimeline` 抽出的滚底/跳转逻辑（含跳转锁） |
| 新增 | `src/components/goal/GoalPanelContent.vue` | 由 `GoalFloatPanel` 抽取的共享目标面板（信息 + 编辑 + 重置） |
| 新增 | `src/components/rightRail/RightRailDrawer.vue` | Drawer 外壳 + icon rail + 内容路由 + `watchSession` 唯一挂载点 |
| 新增 | `src/components/rightRail/PanelBoundary.vue` | 面板级错误边界（`onErrorCaptured` 自实现，防白屏） |
| 新增 | `src/components/rightRail/HistoryTocPanel.vue` | 历史气泡目录面板 |
| 新增 | `src/components/rightRail/HistoryBubble.vue` | 单个气泡（左/右对齐、tooltip、活跃态） |
| 新增 | `src/components/rightRail/RightRailTrigger.vue` | 右侧悬浮触发按钮（+ Goal 迷你徽标） |
| 修改 | `src/utils/turnGroup.ts` | 追加 `TimelineEntry` + `buildTimelineEntries()`（纯新增） |
| 修改 | `src/views/WorkbenchView.vue` | 挂载 Drawer、`to` 指向、`provide` nav |
| 修改 | `src/components/chat/MessageTimeline.vue` | 锚点注册、`setContainer/setOrder`、抽滚底逻辑、跳转锁判定 |
| 修改 | `src/components/goal/GoalFloatPanel.vue` | 内部复用 `GoalPanelContent`；移除 `watchSession()` 调用 |
| 修改 | `src/stores/goalStore.ts` | `watchSession()` 幂等化；`load()` 增加 catch 与序号抑制 |
| 修改 | `src/styles/main.css` | `.rail-tab` / `.toc-bubble` / 锚点闪烁 keyframes |
| 修改（可选） | `src/components/sub/SubHistoryView.vue` | 容器上报 + 锚点 ref（子会话目录跳转） |
| 只读 | `hostBridge.ts / workspaceStore.ts / subSessionStore.ts / useTimelineGroups.ts` | 不新增 API，仅消费 |
