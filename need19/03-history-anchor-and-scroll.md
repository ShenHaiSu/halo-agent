# need19-03 锚点体系与跳转算法（含「自动滚到底」冲突消解）

> 对应总览：[00-README](./00-README.md) R9。前置：[01](./01-architecture-design.md) §4.3。
> 本篇是整个功能**最容易做坏**的一环：主时间轴有自己的滚动容器、自己的自动滚底、自己的 key 体系；跳转必须精确到「某一条消息」，且不能与自动滚底打架。

---

## 1. 问题清单（先想清楚再写代码）

| # | 问题 | 若不处理的后果 |
|---|---|---|
| P1 | Timeline 条目没有 DOM id，只有 Vue `:key` | 无法 `querySelector` 定位 |
| P2 | 滚动容器是 `MessageTimeline` 内部私有 ref | 外部拿不到 `scrollTop/scrollHeight` |
| P3 | 条目高度动态（Markdown 图片懒加载、折叠展开、代码块高亮后重排） | 一次性算出的 `offsetTop` 会失效 |
| P4 | 已有「新增条目 → 自动滚到底」（两段 rAF + 200ms 定时器） | 跳转后若正好有新消息流式写入，视口被拽回底部 |
| P5 | 点击气泡后 Timeline 可能在 `sub.mode==='sub'` 或空会话，锚点不存在 | 静默无效，用户以为坏了 |
| P6 | 压缩上下文（`/compact`）后历史被重写，`id` 变化 | 旧目录条目跳不动（需重新派生即可解决） |
| P7 | tool 消息在时间轴里被并入 turn 组（不单独渲染） | 若目录给 tool 单独出气泡，点了没锚点 |
| P8 | `visibleTurnGroups` 会隐藏「流式中已由实时卡片展示」的工具组 | 目录若用 `allTurnGroups` 会多出条目 |
| P9 | 滚动定位在 `overflow-y-auto` 容器里，不能依赖 `window.scrollY` | 用错 API 滚不动 |

## 2. 总体设计

```text
                 ┌───────────────────────────────┐
                 │ useTimelineNav (模块级单例)     │
                 │  register(key, el) / unregister │
                 │  map: Map<string, HTMLElement>  │
                 │  scrollToAnchor(key, opts)      │
                 │  setContainer(el)               │
                 └───────────▲───────────────────┘
        provide/inject       │
   ┌─────────────────────────┴───────────────────────┐
   │ MessageTimeline                                 │
   │  - 每个条目根元素 :id="anchorIdOf(item.key)"    │
   │  - onMounted/onUpdated → register/unregister    │
   │  - 暴露 scrollContainerRef 给 nav               │
   └─────────────────────────▲───────────────────────┘
                             │ inject
   ┌─────────────────────────┴───────────────────────┐
   │ HistoryTocPanel（或任意跳转发起方）              │
   │  nav.scrollToAnchor(item.anchorKey)             │
   └─────────────────────────────────────────────────┘
```

**关键取舍：为什么用「注册表 + scrollTop 计算」而不是 `el.scrollIntoView()`**

| 方案 | 优点 | 缺点 |
|---|---|---|
| `el.scrollIntoView({block:'start'})` | 一行 | ① 会连带滚动**所有可滚动祖先**，在 CefSharp 里可能把 `n-split` 或 `body` 也滚一格（`body` 已 `overflow:hidden`，但 Drawer 内滚动容器可能被带偏）；② 无法控制「留白偏移」（希望目标停在顶部下方 12px，而不是贴边）；③ 与「滚动容器内定位」语义不一致 |
| `container.scrollTop = el.offsetTop - pad` | 精确、可留白、不惊动祖先 | 需要正确的 `offsetTop`（相对定位祖先链），见 §4.2 |
| `container.scrollTo({top, behavior:'smooth'})` | 平滑 | Chrome109 支持；但**流式渲染期间**平滑动画会与列表重排互相打断 → 用「先 smooth，若 200ms 内条目标记变化则立即 snap」策略 |

**选择**：以 `scrollTop` 计算为主，`scrollTo({behavior})` 承载动画；兜底 `scrollIntoView({block:'nearest'})`。

## 3. `useTimelineNav`（锚点注册表）

### 3.1 接口定义

```ts
// src/composables/useTimelineNav.ts
import type { InjectionKey } from 'vue';
import type { TimelineEntry } from '@/utils/turnGroup';

export type JumpBehavior = 'auto' | 'smooth';

export interface ScrollToAnchorOptions {
  behavior?: JumpBehavior;        // 默认 'smooth'
  highlight?: boolean;            // 默认 true
  highlightMs?: number;           // 默认 1200
  paddingTop?: number;            // 默认 12
  /** 锚点缺失时的兜底：按索引找最近的更早锚点；默认 true */
  fallbackNearest?: boolean;
}

export interface TimelineNav {
  register(key: string, el: HTMLElement): void;
  unregister(key: string): void;
  clear(): void;
  setContainer(el: HTMLElement | null): void;
  /** 返回是否成功定位（false = 锚点不存在/容器未就绪） */
  scrollToAnchor(key: string, opts?: ScrollToAnchorOptions): boolean;
  /** 当前条目指纹（用于判断「跳转期间列表是否变化」） */
  entryKeys(): string[];
}

export const TIMELINE_NAV_KEY: InjectionKey<TimelineNav> = Symbol('halo.timelineNav');
```

### 3.2 实现要点

```ts
const registry = new Map<string, HTMLElement>();
const domIdOf = (key: string) => `halo-msg-${key.replace(/[^A-Za-z0-9_-]/g, '_')}`;

function register(key: string, el: HTMLElement) {
  registry.set(key, el);
  if (!el.id) el.id = domIdOf(key);
}
function unregister(key: string) { registry.delete(key); }
function clear() { registry.clear(); }
```

* **`key` 来源唯一**：`TimelineEntry.key`（`u-<id>` / `s-<id>` / `<assistantId>` / `tool-<id>`），与 `MessageTimeline.vue:212-222` 完全一致。
* **DOM id 清洗**：后端 messageId 形如 `msg_20261019_xxxx`，但仍做白名单替换，防止未来出现特殊字符导致 `querySelector` 失败。
* **非响应式**：`Map` 不包 `reactive`，避免每次注册触发渲染；生命周期与组件挂载一一对应。
* **注册时机**：用 `v-for` 的 `:ref` 回调（Vue3 支持的函数式 ref），比 `onUpdated` 遍历更精确：

  ```vue
  <div v-for="item in timelineItems" :key="item.key" :ref="(el) => bindAnchor(item.key, el)">
  ```
  ```ts
  function bindAnchor(key: string, el: Element | ComponentPublicInstance | null) {
    if (el instanceof HTMLElement) nav.register(key, el);
    else nav.unregister(key);   // 卸载/复用为 null
  }
  ```
  > ⚠️ 函数式 ref 每次重渲染都会重新调用 → `register` 必须是幂等的（`Map.set` 天然幂等），不要在里面做重活。

### 3.3 provide / inject 挂载位置

```ts
// WorkbenchView.vue
const nav = createTimelineNav();
provide(TIMELINE_NAV_KEY, nav);
```

* `MessageTimeline` 用 `inject` 拿到并在 `onMounted` 后 `nav.setContainer(scrollContainerRef.value)`，`onUnmounted` 时 `nav.clear(); nav.setContainer(null)`。
* `HistoryTocPanel`（在 Drawer 内）**不是在 `MessageTimeline` 的子树**里，但与 `WorkbenchView` 同树 → `inject` 可达。
* **兜底**：`useTimelineNav()` 若 inject 到 `null`，返回一个 no-op 实现并 `console.warn`（避免 Drawer 在无 Timeline 的场景下报错，例如单测渲染）。

## 4. 跳转算法

### 4.1 主流程

```text
scrollToAnchor(key, opts):
1. 若 sub.mode === 'sub' → 由调用方先决定是否允许（子会话只读，允许跳转，由 SubHistoryView 注册自己的锚点，见 §7）
2. el = registry.get(key)
3. 若 !el 且 opts.fallbackNearest：
     keys = entryKeys(); i = keys.indexOf(key);
     从 i-1 向前找第一个已注册的 key；
     若向前没有，从 i+1 向后找；仍没有 → return false
4. 若容器为 null → el.scrollIntoView({block:'center'}) 兜底 → return true
5. rect = el.getBoundingClientRect(); cRect = container.getBoundingClientRect();
   target = container.scrollTop + (rect.top - cRect.top) - (opts.paddingTop ?? 12)
   clamp(target, 0, container.scrollHeight - container.clientHeight)
6. container.scrollTo({ top: target, behavior: opts.behavior ?? 'smooth' })
7. opts.highlight → el.classList.add('timeline-anchor-flash')；1200ms 后移除
8. 写入 uiStore.jumpLockUntil = performance.now() + 1500
   return true
```

**为什么用 `getBoundingClientRect()` 差值而不是 `el.offsetTop`**：
`offsetTop` 是相对「最近的 positioned 祖先」，时间轴里各层 div 的 `position` 会随实现变化（`flex` 容器不影响，但 `goal-float` 等 absolute 元素的存在会让人误判）。`rect` 差值永远正确，代价是一次 layout 读取，跳转是低频操作，完全可接受。

### 4.2 滚动容器识别

`MessageTimeline` 的滚动容器是模板第一行那个 `div`（`:2`，`overflow-y-auto`）。**不要**用 `closest('.overflow-y-auto')` 猜，直接由组件 `setContainer(scrollContainerRef.value)` 显式传入。

> **注意**：`SubHistoryView` 的滚动容器是它自己的根 div（`SubHistoryView.vue:2`），见 §7。

### 4.3 锚点缺失的兜底（P5/P6）

| 情形 | 现象 | 兜底 |
|---|---|---|
| 压缩后 id 变化 | 目录条目是旧派生数据？ | **不可能**：目录是 `computed`，压缩触发 `loadSessionMessages` → 新数组 → 目录自动重算 |
| 目录刚渲染、Timeline 还没挂载 | 点击无反应 | `scrollToAnchor` 返回 false → 面板 `message.warning('消息尚未渲染，请稍后重试')` |
| 目标条目被折叠/隐藏（hidden tool 组） | 找不到 | `fallbackNearest` 向前找，落点为其所属 assistant 正文 |
| 子会话刚进入尚未拉完消息 | 无锚点 | 同上；面板按钮置灰直到 `messages.length>0` |

### 4.4 高亮策略

* 使用 **class 增删**（`timeline-anchor-flash`），不改变元素内联样式（避免与 Markdown 容器样式冲突）。
* 同时只允许一个高亮：`let activeEl: HTMLElement | null`，切走时移除旧 class（防止快速连点留下多个）。
* `prefers-reduced-motion: reduce` 时降级为静态 outline（已在 [01](./01-architecture-design.md) §5.3 定义）。
* 高亮元素若是「turn 容器」（内含思考/工具/正文），高亮的是**整个轮次容器**，视觉上更有辨识度。

### 4.5 跳转后「保持位置」而不被自动滚底拉走（P4 关键）

`MessageTimeline` 现有自动滚底逻辑（`:459-497`）：

```ts
function scrollTimelineToBottomIfNeeded(): boolean {           // 只在「还没到底」时滚
  const maxScroll = el.scrollHeight - el.clientHeight;
  if (maxScroll <= 0) return false;
  if (maxScroll - el.scrollTop <= 1.5) return false;
  el.scrollTop = el.scrollHeight;
  return true;
}
watch(() => timelineItems.value.length, () => nextTick(() => scheduleScrollTimelineToBottom()));
watch(() => workspaceStore.loadingMessages, (loading) => { if (!loading) nextTick(schedule); });
```

**它有两条会"抢"视口的路径**：① 条目数变化；② `loadingMessages` 由 true→false（即每次 `loadSessionMessages` 都会滚到底）。

**消解方案（三层）**：

1. **跳转锁**：`scrollToAnchor` 成功后写 `uiStore.jumpLockUntil = performance.now() + 1500`。
   `scrollTimelineToBottomIfNeeded()` 开头加：

   ```ts
   if (performance.now() < uiStore.jumpLockUntil) return false;
   ```
   用 `performance.now()` 而非 `Date.now()`，避免系统时钟调整影响。

2. **用户交互即解锁**（不能锁太久惹人烦）：监听容器 `wheel / touchstart / keydown(PageUp/PageDown/Home/End)`，首次触发时把 `jumpLockUntil = 0`：

   ```ts
   function onUserScrollIntent() { uiStore.jumpLockUntil = 0; }
   ```
   > 只在 `passive: true` 下监听，不影响滚动性能。**注意**：程序化 `scrollTo` 不会触发 `wheel`，所以不会自解锁。

3. **跳转时一次性取消待执行的滚底任务**：
   ```ts
   function cancelScheduledScrollToBottom() {
     if (scrollBottomRaf) cancelAnimationFrame(scrollBottomRaf);
     if (scrollBottomTimer !== null) window.clearTimeout(scrollBottomTimer);
     scrollBottomRaf = 0; scrollBottomTimer = null;
   }
   ```

**行为保证**：
* 跳转后 1.5s 内即使有新消息到达，视口保持；
* 1.5s 后或用户主动滚动 → 恢复原有「新消息自动到底」行为；
* 用户在底部且不发新消息 → 全程无差异（现有逻辑本来就只在「未到底」时才滚）。

### 4.6 跳转中的「条目高度变化」处理（P3）

平滑滚动期间，若图片加载完成导致目标上方高度变化，落点会偏。处理：

```ts
// 跳转后在 250ms / 600ms 各做一次「校正」
function settleJump(key: string, targetTop: () => number) {
  [250, 600].forEach((ms) => window.setTimeout(() => {
    if (performance.now() > uiStore.jumpLockUntil) return;   // 已被用户接管
    const el = registry.get(key); if (!el) return;
    const c = container; if (!c) return;
    const delta = Math.abs((c.scrollTop + el.getBoundingClientRect().top - c.getBoundingClientRect().top - 12) - c.scrollTop);
    if (delta > 4) c.scrollTop = targetTop();                 // snap 校正，不再 smooth
  }, ms));
}
```

> 简化版：直接在 `img` 上未做懒加载（现有 `n-image` 无 `lazy`，见 `MessageTimeline.vue:25-33`），因此该校正只在「代码块高亮异步完成」时才有意义，属于**可选增强**。施工时先不做，若验收发现偏移再加（[06](./06-implementation-and-acceptance.md) 列为 P3 可选）。

## 5. `MessageTimeline` 改造清单（最小侵入）

| # | 改动 | 位置 | 说明 |
|---|---|---|---|
| T1 | 条目根元素加函数式 `:ref` 注册 | 模板 `:14-82`（三处：user / system / turn 的根 div，以及在 `v-for` 容器上统一绑定） | **推荐**在最外层 `v-for` 的 `<template>` 无法挂 ref，因此在三种分支的根 `div` 上分别绑定 `:ref="bindAnchor(item.key)"`（用高阶函数） |
| T2 | `scrollContainerRef` 上报给 nav | `onMounted` | `nav.setContainer(scrollContainerRef.value)` |
| T3 | 抽 `useTimelineScroll`（可选但推荐） | `:458-503` | 把「滚底 + 跳转锁」逻辑抽到 composable，`MessageTimeline` 只调用；**行为不变**是硬约束 |
| T4 | 注册表清理 | `onUnmounted` | `nav.clear(); nav.setContainer(null)` |
| T5 | `timelineItems` 改用 `buildTimelineEntries` | `:199-225` | 纯重构，输出必须逐项相等（验收 R-T1） |

**T1 的具体写法（避免踩坑）**：

```vue
<!-- 分支 1：user -->
<div v-if="item.kind === 'user'" class="flex justify-end" :ref="setAnchor(item.key)">
  ...
</div>
<!-- 分支 2：system -->
<div v-else-if="item.kind === 'system'" :ref="setAnchor(item.key)" ...>
<!-- 分支 3：turn -->
<div v-else class="space-y-2 min-w-0" :ref="setAnchor(item.key)">
```

```ts
function setAnchor(key: string) {
  return (el: Element | ComponentPublicInstance | null) => {
    if (el instanceof HTMLElement) nav.register(key, el);
    else nav.unregister(key);
  };
}
```

> **为什么不在最外层包一层 wrapper**：会改变 `space-y-4` 的间距语义（每个 item 从 1 个节点变成 wrapper），可能引起主界面像素差；分支上挂 ref 零结构变化。

## 6. 目录条目 → 锚点 key 的映射规则

由 [05](./05-quick-jump-panel.md) 的 `buildHistoryToc()` 负责，规则固定为：

| `TimelineEntry.kind` | 气泡 | `anchorKey` |
|---|---|---|
| `user` | 右侧气泡 | `item.key`（`u-<id>`） |
| `turn` | 左侧气泡 | `item.key`（`<assistantId>` 或 `tool-<id>`） |
| `system` | 居中小字（默认隐藏，可开「显示系统消息」） | `item.key`（`s-<id>`） |

**绝不为 tool 消息单独出气泡**（P7）：tool 在时间轴里属于 turn 组内部，锚点只在组根。

## 7. 子会话模式（`sub.mode === 'sub'`）

* 主界面渲染 `SubHistoryView`，它用自己的滚动容器与 `buildTimelineItems`。
* **方案**：让 `SubHistoryView` 也接入 `TimelineNav`（同样的 `provide` 来源，因为在 `WorkbenchView` 下）：
  * 容器：根 div（`SubHistoryView.vue:2`）；
  * 锚点 key：使用**同一个**简化版 `buildTimelineItems` 产出的 `item.key`（`m.id` 或 `assistant_<n>` 兜底），目录在子会话模式下也必须用**同一个** `buildTimelineItems` 派生，才能对齐。
* **目录派生分叉**（在 `HistoryTocPanel` 内部）：

  ```ts
  const entries = computed<TimelineEntry[]>(() => {
    if (sub.mode === 'sub') return toEntriesFromSimple(buildTimelineItems(sub.messages));
    return buildTimelineEntries(ws.sessionMessages, visibleTurnGroups);
  });
  ```

  `toEntriesFromSimple()` 把简化的 `TimelineItem`（`{key,kind,msg,group}`）转成 `TimelineEntry`（字段同构，直接映射即可）。
* **成本/收益**：`SubHistoryView` 需要加 5 行（容器上报 + ref 绑定）。若本期不想动它，则目录在子会话模式下**只读展示、点击禁用**（tooltip：「子会话跳转下期支持」）。**建议**：直接做，代价极小，体验一致。

## 8. 验收点（本篇专属）

| # | 检查 | 通过标准 |
|---|---|---|
| A1 | 首条/中间/末条气泡点击 | 目标消息顶部距容器顶 ~12px；高亮 1.2s；控制台无报错 |
| A2 | 长会话（≥300 条）连续点击 10 个气泡 | 每次落点误差 ≤ 8px；无「跳一半停住」 |
| A3 | 跳转期间发送新消息（或 Agent 流式中） | 视口不被拽到底部；流式结束后也不动（锁期内） |
| A4 | 跳转后用户 wheel 上滑 | 立即恢复「新消息自动到底」语义（锁被解除） |
| A5 | 快速连点两个气泡 | 只保留最后一次高亮；不出现两个闪烁元素 |
| A6 | 子会话模式 | 气泡与 `SubHistoryView` 条目一一对应，点击可定位 |
| A7 | `chatWidthMode` = centered / full | 两种模式落点均正确（用 rect 计算天然不受限宽影响） |
| A8 | 窗口缩放后点击 | 落点正确（每次跳转实时取 rect，不缓存坐标） |
| A9 | 压缩上下文后 | 目录自动刷新为新历史；点击正常（旧 key 不存在也不会误跳） |
