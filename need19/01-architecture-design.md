# need19-01 架构设计：组件树、状态分布、数据流与视觉规格

> 对应总览：[00-README](./00-README.md) §3 的展开。
> 本篇只回答四个问题：**放哪里（挂载点）**、**拆成什么（组件树）**、**数据从哪来（状态与流向）**、**长什么样（视觉规格）**。

---

## 1. 挂载点决策：为什么是 `WorkbenchView` 而不是 `App.vue`

| 候选 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| A. `App.vue` 根 `div` 内（与 `SettingsCenterDrawer` 同级） | 实现最省事 | Drawer 默认 Teleport `body` 或覆盖整个窗口，会**遮住左侧工作区/会话侧栏**，且与 `n-split` 拖动条冲突 | ❌ |
| B. `App.vue` 的 `n-split #2` 内部（WorkbenchTopBar 同级） | 范围正确 | 需要 `App.vue` 传入 anchor 元素 ref，跨层耦合；`WorkbenchTopBar` 的层级与 `z-index` 需重新梳理 | ⚠️ 备选 |
| **C. `WorkbenchView.vue` 根节点内（本期采用）** | 根节点已是 `relative overflow-hidden`，Drawer `to` 直接绑定根节点即可只覆盖右工作台；组件自洽，`App.vue` 零改动 | 需要给作用域内元素加 `position/z-index` 约定（见 §3.3） | ✅ |

**结论**：采用 C。`App.vue` 完全不改，降低回归面（`n-split` 分栏比例、状态栏、SettingsCenterDrawer 均不动）。

### 1.1 `to` 绑定方式（Chrome109 安全写法）

```vue
<!-- WorkbenchView.vue -->
<template>
  <div ref="hostRef" class="flex-1 min-h-0 flex flex-col bg-app overflow-hidden relative" data-testid="workbench-view">
    <MessageTimeline v-if="sub.mode === 'main'" />
    <SubHistoryView v-else />
    <PromptComposer v-if="sub.mode === 'main'" />
    <n-alert v-else type="info" class="mx-4 mb-2 text-xs">子会话历史只读；返回主会话继续对话</n-alert>
    <GoalFloatPanel />
    <AskUserModal />
    <RightRailTrigger />
    <RightRailDrawer :to="hostRef" />
  </div>
</template>
```

* prop 类型：`to?: string | HTMLElement`（Naive 定义 `to: [String, Object]`，见 `naive-ui/es/drawer/src/Drawer.mjs` 的 `drawerProps.to`）。
* **首次渲染 `hostRef` 为 `null` 时**必须回退 `'body'`（虽然 Drawer 初始 `show=false` 不渲染内容，但模板求值会读 prop）：
  ```ts
  const drawerTo = computed(() => hostRef.value ?? 'body');
  ```
  并在 `onMounted` 后经 `nextTick` 自然纠正。**不要**在 `setup` 阶段就 `document.querySelector`。

## 2. 组件树与职责边界

```text
WorkbenchView.vue                        （宿主：提供 to / 挂载 Drawer 与触发器）
├─ MessageTimeline.vue                   （既有：★ 增加锚点注册 + provide TimelineNav）
│    └─ MessageContextMenu.vue           （既有，不动）
├─ PromptComposer.vue                    （既有，不动）
├─ GoalFloatPanel.vue                    （既有：★ 内部改为 GoalPanelContent；关闭态仍可拖拽）
│    └─ GoalPanelContent.vue             （★新增：目标唯一 UI 实现，mode="float" | "drawer"）
├─ AskUserModal.vue                      （既有，不动）
├─ RightRailTrigger.vue                  （★新增：右侧竖向悬浮触发按钮，含 Goal 迷你进度徽标）
└─ RightRailDrawer.vue                   （★新增：Drawer 外壳 = rail + 内容路由 + watchSession 唯一挂载点）
     └─ PanelBoundary.vue                （★新增：内容区错误边界，防止单个面板崩溃拖垮整个 Drawer）
     ├─ rail：<button v-for="tab in tabs">（仅 icon + n-tooltip + 角标）
     ├─ GoalPanelContent.vue   （tab = 'goal'，mode="drawer"）
     └─ HistoryTocPanel.vue    （★新增，tab = 'toc'）
          ├─ 头部工具条（过滤 / 轮次星标 / 统计）
          └─ HistoryBubble.vue  （★新增，v-for 渲染）
```

### 2.1 每层职责（严禁越界）

| 组件 | 允许做 | 不允许做 |
|---|---|---|
| `WorkbenchView` | 提供 DOM 宿主、决定 Timeline 是否挂载 | 持有 Drawer 状态（应在 `uiStore`） |
| `RightRailDrawer` | Drawer 外壳、rail 切换、调用 `goalStore.watchSession()`、`goalStore.load()` | 直接 `getHostBridge()` 拉消息；直接操控 Timeline DOM |
| `HistoryTocPanel` | 由 `sessionMessages` / `subSessionStore.messages` **computed** 出气泡列表；调用 `useTimelineNav().scrollToAnchor()` | 自己保存消息副本；自己 `scrollIntoView` |
| `HistoryBubble` | 展示、hover tooltip、键盘 Enter 触发 `emit('jump', item)` | 内部计算跳转坐标 |
| `MessageTimeline` | 渲染 + 注册锚点 + 实现 `TimelineNav.scrollToAnchor` | 关心 Drawer 是否存在 |
| `GoalPanelContent` | 目标渲染/编辑/保存/重置（`goalStore` API） | 自己 `watch` 会话切换（由父级负责） |

## 3. 状态分布（Pinia / computed / 局部）

### 3.1 新增 `uiStore`（唯一新增 store）

```ts
// src/stores/uiStore.ts
export type RightRailTab = 'goal' | 'toc';
export const useUiStore = defineStore('ui', () => {
  const rightRailOpen = ref(false);
  const rightRailTab = ref<RightRailTab>('toc');
  const rightRailWidth = ref(360);                 // px, 320~720
  const jumpLockUntil = ref(0);                    // performance.now() 时间戳，见 03 §5
  const tocFilter = ref<'all' | 'user' | 'assistant'>('all');
  const tocOnlyRounds = ref(false);                // 只显示「用户提问 → 该轮回复」两两一对
  // 持久化键：halo.ui.rightRail.v1（open 不持久化，见 04 §6）
  ...
});
```

**为什么不放 `appearanceStore`**：`appearanceStore` 语义是「外观与字体」，且被 `App.vue` 的 `buildHaloThemeOverrides` 消费；把运行时面板状态塞进去会污染主题计算路径。`uiStore` 更内聚。

### 3.2 状态表

| 状态 | 位置 | 派生自 | 备注 |
|---|---|---|---|
| Drawer 开关 / 当前 Tab / 宽度 | `uiStore` | 用户操作 | 宽度持久化，开关不持久化 |
| `jumpLockUntil` | `uiStore` | 跳转动作写入 | 供 Timeline 的自动滚底判定 |
| 目标数据 | `goalStore.goal` | 后端 `goal.json` | 已有，不改结构 |
| 目标编辑态 | `GoalPanelContent` 局部 `ref` | 用户输入 | 抽出后与原浮卡一致 |
| 历史消息（主） | `workspaceStore.sessionMessages` | `getSessionMessages` | 唯一来源 |
| 历史消息（子） | `subSessionStore.messages` | `getSubSessionMessages` | 子会话只读 |
| 目录条目 | `HistoryTocPanel` 内 `computed` | 见 §4 | 不落地、不缓存 |
| 锚点注册表 | `useTimelineNav` 模块内 `Map` | Timeline 渲染 | 非响应式（避免每帧触发） |
| 当前活跃锚点 | `RightRailDrawer`/`HistoryTocPanel` 局部 `ref` | 跳转动作 / 可选 IntersectionObserver | 仅用于气泡高亮 |

### 3.3 层级（z-index）约定

| 层 | 元素 | z-index | 说明 |
|---|---|---|---|
| 1 | Timeline / Composer | 默认（0） | — |
| 40 | `GoalFloatPanel` | 40（既有） | 不变 |
| 50 | `MessageContextMenu` | 50（既有） | 不变 |
| 60 | `RightRailTrigger` | 60 | 在 Timeline 之上、Drawer 之下 |
| 61 | `RightRailDrawer`（Naive 自管） | 由 `n-drawer` 的 `zIndexable` 指令自动分配（`Drawer.mjs` 渲染处） | **不要**手写 `z-index` 覆盖；如需与设置中心（同为 n-drawer，根级）协调，保持二者独立即可 |
| 70+ | `AskUserModal`（n-modal，body 层） | Naive modal 自动 | 保证「问询弹窗永远在最上层」，不被 Drawer 遮挡 |
| 99 | `n-message` / `n-notification` | Naive 自动 | 同上 |

> **红线**：Drawer 内不要 `position: fixed` 去抢遮罩层；一切浮层用 `n-popover/n-tooltip/n-dropdown`（它们会被 Teleport 到 body 并按 zindex 规则插队）。

## 4. 数据流（三条主链路）

### 4.1 目录链路（读 → 派生 → 点击）

```text
workspaceStore.sessionMessages ─┐
subSessionStore.messages ───────┤ (按 mode 二选一)
                                ▼
        buildTimelineItems(msgs)  ← 复用 useTimelineGroups.ts（与 MessageTimeline 同源）
                                ▼
        buildHistoryToc(items)    ← 新增纯函数：过滤/计数/配对/标签
                                ▼
        HistoryTocPanel (computed toc)
                                ▼  点击
        useTimelineNav().scrollToAnchor(key, { behavior, highlight:true })
                                ▼
        MessageTimeline 注册表 key → HTMLElement（缺失则退化为索引上溯 → 见 03 §4.3）
                                ▼
        scroll 容器 scrollTop 计算 + 高亮 class 500~1200ms
```

* **为什么不直接扫 DOM**：DOM 里没有稳定 id（`timelineItems` 只有 Vue `:key`），扫文本会与 Markdown 渲染结果不一致；注册表是零成本的。
* **为什么两边都用 `buildTimelineItems`**：`MessageTimeline` 自己维护了一个**加过滤**的版本（`visibleTurnGroups` 去掉流式中已隐藏的 tool 组）。目录必须与「屏幕上真实存在的条目」对齐 → 改造点见 §4.3。

### 4.2 Goal 链路

```text
RightRailDrawer.onMounted → goalStore.watchSession()（幂等）
                          → watch(activeWorkspaceId/activeSessionId) → goalStore.load()
GoalPanelContent 编辑 → goalStore.save()/clear() → 后端 goal.json → 事件 goal:updated
                                   ↓
goalStore.applyEvent → goal / updatedFlash（浮卡与抽屉同时闪烁）
```

### 4.3 与 `MessageTimeline` 的对接契约（关键！）

`MessageTimeline` 现有的条目计算是「两段式」：

```ts
const allTurnGroups      = computed(() => buildTurnGroups(persistedMessages))
const visibleTurnGroups  = computed(() => /* 去掉 hiddenToolCallIds + 空组 */)
const timelineItems      = computed(() => /* 按 jsonl 顺序展开成 user/system/turn */)
```

**契约**：把第三段（`timelineItems` 的构造）**下沉为纯函数** `buildTimelineEntries(messages, groups)`，放 `src/utils/turnGroup.ts`。

> ⚠️ **命名冲突提醒（实测证据）**：`composables/useTimelineGroups.ts` 里已有一个**简化版** `buildTimelineItems(msgs)`（无 hidden 过滤、无 group 兜底），当前消费者是 `SubHistoryView.vue:42,60`（子会话只读视图），而 `useTimelineGroups()` 组合函数本身全仓无引用。
> 两者语义不同（简化版不吞 hidden tool、不做孤立组兜底），**不能**互相替换，也不能同名。
> 施工时：
> 1. 新增 `buildTimelineEntries(messages, groups)` 放 `src/utils/turnGroup.ts`（显式吃 `TurnGroup[]`，用于主时间轴与目录）；
> 2. `useTimelineGroups.ts` 保持原样（服务子会话视图），仅在文件头补一行注释说明「简化版，主时间轴请用 `buildTimelineEntries`」。
>
> 本文档后续统一命名为 **`buildTimelineEntries()`**。

签名与语义（与 `MessageTimeline.vue:192-225` 完全一致，仅抽出）：

```ts
export interface TimelineEntry {
  key: string;                       // 'u-<id>' | 's-<id>' | group.key（'<assistantId>' 或 'tool-<id>'）
  kind: 'user' | 'system' | 'turn';
  msg?: UnifiedChatMessage;
  group?: TurnGroup;
}
export function buildTimelineEntries(messages: UnifiedChatMessage[], groups: TurnGroup[]): TimelineEntry[];
```

目录面板拿到的是**同一份** `TimelineEntry[]`（`MessageTimeline` 通过 `TimelineNav.setEntries()` 推送，或目录自己用同样的两个 computed 独立算一份——**推荐后者**，理由见下）。

**推荐「各自独立算一份」而非互相推送**，原因：
1. `MessageTimeline` 在 `sub.mode==='sub'` 时不挂载，目录仍需工作；
2. 两个 computed 共享同一输入引用，Pinia 缓存 + Vue 依赖追踪下开销是 O(n) 一次，n ≤ 数千，可接受（`buildTurnGroups` 本身就是 O(n)）；
3. 避免生命周期时序问题（Timeline 先卸载把目录的数据清了）。

**唯一必须共享的是「key ↔ DOM」映射**，这通过 `useTimelineNav` 的注册表实现（[03](./03-history-anchor-and-scroll.md) §3）。

## 5. 视觉规格（视觉稿用 Tailwind 语义 token 表达）

### 5.1 外观总表

| 元素 | 规格 |
|---|---|
| Drawer 宽度 | 默认 `360px`，可拖拽 `320~720px`，`max-width: min(720px, 86vw)` |
| Drawer 背景 | `var(--bg-surface)`；与主界面 `var(--bg-app)` 形成一层抬升 |
| 左边线 | `1px solid var(--border-subtle)`（覆盖在 Drawer 左侧，表示「从右侧滑出」） |
| icon rail 宽度 | `44px`（紧凑）/ `48px`（默认，与 Naive `heightMedium=30px` 的按钮中心对齐更好看） |
| icon rail 背景 | `var(--bg-sidebar)`（与左侧工作区侧栏同色，形成「两侧栏」呼应） |
| icon 尺寸 | `18px`（lucide）, 按钮 `36×36`，圆角 `8px`，间距 `6px` |
| icon 选中态 | 左侧 `2px` 高亮条 `var(--accent-primary)` + 底色 `rgba(59,130,246,.14)`（亮色 `#eff6ff`）；由 CSS 变量统一，见 §5.3 |
| icon 未选中 | `color: var(--text-secondary)`；hover → `var(--text-primary)` + `rgba(255,255,255,.04)` |
| 内容区内边距 | `10px 10px 8px`（对齐 `SettingsCenterDrawer` 的紧凑密度） |
| 内容区标题行 | 高 `34px`：图标 + 标题（13px/600）+ 右侧操作区（如「过滤」下拉） |
| 气泡最大宽度 | `78%`（内容区宽度），最小 `auto` |
| 气泡字号 | `var(--app-font-size) - 1px`（即 `.app-text-small`），行高 1.45 |
| 气泡内边距 | `6px 9px`，圆角 `10px`，方向性圆角（§5.2） |
| 气泡间距 | `6px`；同一轮内的连续气泡（assistant → 其后的 system）额外 `2px` |
| 动画 | Drawer 用 Naive 原生 `slide-in-from-right-transition`；气泡 hover `background-color .12s`；锚点高亮 1200ms（见 §5.4） |

### 5.2 气泡左右语义（配色与形状）

| 角色 | 对齐 | 背景 | 文字 | 圆角 | 额外 |
|---|---|---|---|---|---|
| `user` | 右对齐（`flex-row-reverse`） | `rgba(59,130,246,.16)`（亮色 `#eff6ff`），左边框 `2px var(--accent-primary)` | `var(--text-primary)` | `10px 10px 2px 10px`（右下角收紧） | 前缀小图标 `UserRound` 或什么都不加，靠位置区分 |
| `turn`（assistant） | 左对齐 | `var(--bg-elevated)`，右边框 `2px var(--border-subtle)` | `var(--text-primary)` | `10px 10px 10px 2px`（左下角收紧） | 若该轮含工具调用：右上角小徽标 `🧠 n`；若被截断（`turnNotice`）：徽标 `⚠` |
| `system` | 居中 | 透明，虚线底 `border-dashed` | `var(--text-secondary)` | `6px` | 字号 `-2px`，`font-mono` |
| `summary`（压缩摘要，role=user 且 `content` 以压缩标记开头时） | 居中 | `rgba(245,158,11,.12)` | `var(--text-secondary)` | `8px` | 徽标 `Σ` |

> 颜色基于 `:root` 暗色；亮色用 `html.light` 覆盖（与 `main.css:27-42` 同套变量）。**禁止**写 `text-blue-500` 之类的固定色，统一在 `RightRail*` 组件的 `<style scoped>` 里用 `var()`，或新增 `main.css` 顶层类（§5.3）。

### 5.3 新增 CSS 基类（放 `main.css` 末尾，避免 Tailwind 任意值污染）

```css
/* need19 右侧栏：icon rail */
.rail-tab { width:100%; height:36px; display:flex; align-items:center; justify-content:center;
  position:relative; border-radius:8px; color:var(--text-secondary); transition:background-color .12s,color .12s; }
.rail-tab:hover { color:var(--text-primary); background:rgba(255,255,255,.05); }
.rail-tab.is-active { color:var(--accent-primary); background:rgba(59,130,246,.14); }
.rail-tab.is-active::before { content:""; position:absolute; left:-6px; top:8px; bottom:8px; width:2px;
  border-radius:2px; background:var(--accent-primary); }
html.light .rail-tab:hover { background:#f1f5f9; }
html.light .rail-tab.is-active { background:#eff6ff; }

/* need19 目录气泡 */
.toc-bubble { max-width:78%; padding:6px 9px; border-radius:10px; font-size:calc(var(--app-font-size) - 1px);
  line-height:1.45; border:1px solid transparent; transition:background-color .12s, border-color .12s;
  overflow-wrap:anywhere; word-break:break-word; cursor:pointer; }
.toc-bubble--user { background:rgba(59,130,246,.16); border-left:2px solid var(--accent-primary); border-radius:10px 10px 2px 10px; }
.toc-bubble--assistant { background:var(--bg-elevated); border-right:2px solid var(--border-subtle); border-radius:10px 10px 10px 2px; }
.toc-bubble.is-active { border-color:var(--accent-primary); box-shadow:0 0 0 2px rgba(59,130,246,.22); }
html.light .toc-bubble--user { background:#eff6ff; }

/* need19 锚点高亮 */
@keyframes halo-anchor-flash { 0%{background-color:rgba(59,130,246,.22);} 100%{background-color:transparent;} }
.timeline-anchor-flash { animation:halo-anchor-flash 1.2s ease-out 1; border-radius:8px; }
@media (prefers-reduced-motion: reduce) { .timeline-anchor-flash { animation:none; outline:2px solid var(--accent-primary); } }
```

### 5.4 与「沉浸感」相关的细节

1. **不抢焦点**：Drawer 打开后焦点留在触发按钮上（`autoFocus=false` + `trapFocus=false`），避免打字时被 Drawer 抢走光标（详见 [04](./04-drawer-spec-and-behavior.md) §5）。**但**目录气泡需要键盘可达：靠 `tabindex="0"` 而非容器焦点陷阱。
2. **可拖拽宽度**：`resizable` + `defaultWidth=360`，`onUpdate:width` 写回 `uiStore.rightRailWidth`（节流 100ms 落 localStorage）。
3. **空态一致性**：两个面板的空态都用同一套 `EmptyHint` 视觉（图标 + 一行说明 + 一个次要动作按钮），不要各自为政。

## 6. 与既有设计决策的兼容性检查

| 既有决策 | 来源 | 本期是否冲突 | 处理 |
|---|---|---|---|
| 主界面宽度 `centered/full` 由 `appearanceStore.chatWidthMode` 控制 | `theme/appearance.ts:19` | 不冲突（overlay 不改轨道宽度） | 无 |
| 设置中心用 `n-drawer` + `n-tabs placement=left` | `SettingsCenterDrawer.vue` | 不冲突 | 参数为「仅 icon」，用自绘 rail 而非 `n-tabs`；两者并存时各自独立 |
| 右键菜单 fixed z-50 | `MessageContextMenu.vue:70-71` | 可能被 Drawer 覆盖（Drawer z-index 更高） | 可接受（Drawer 打开时用户不会右键主界面；且 Drawer 打开了 `blockScroll` 见 04 §5.4） |
| `AskUserModal` 用 `n-modal` 挂 body | `AskUserModal.vue:2` | 必须仍在最上层 | Naive modal zindex 递增加 1，天然胜出；验收项 M6 |
| 「进程单实例」need18 | `docs/need18` | 无关 | 无 |
