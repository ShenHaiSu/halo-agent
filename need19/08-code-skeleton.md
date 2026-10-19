# need19 右侧边栏 · 施工蓝图与关键代码骨架

> 配套文档：[00](./00-README.md) ~ [07](./07-edge-cases.md)。
> 本篇提供**可直接照抄的骨架代码**，避免施工时在命名/接口上反复摇摆。所有代码为「签名 + 关键实现 + 注释锚点」，非最终产物（具体类名/样式以各篇为准）。

---

## 1. `src/stores/uiStore.ts`

```ts
/**
 * need19：右侧边栏（Drawer）UI 状态。仅"界面偏好"，不承载业务数据。
 * 持久化键：halo.ui.rightRail.v1（open 不持久化）
 */
import { defineStore } from 'pinia';
import { ref, computed, watch } from 'vue';

export type RightRailTab = 'goal' | 'toc';
export type TocRoleFilter = 'all' | 'user' | 'assistant';

const STORAGE_KEY = 'halo.ui.rightRail.v1';
const MIN_W = 320;
const MAX_W = 720;

interface Persisted {
  tab: RightRailTab;
  width: number;
  hintShown: boolean;
  tocRole: TocRoleFilter;
  tocRoundHeadsOnly: boolean;
  tocSkipSystem: boolean;
}

function clampWidth(v: unknown): number {
  const n = Number(v);
  if (!Number.isFinite(n)) return 360;
  return Math.max(MIN_W, Math.min(MAX_W, Math.round(n)));
}

function loadPersisted(): Persisted {
  const fallback: Persisted = {
    tab: 'toc', width: 360, hintShown: false,
    tocRole: 'all', tocRoundHeadsOnly: false, tocSkipSystem: true,
  };
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return fallback;
    const o = JSON.parse(raw) as Partial<Persisted>;
    return {
      tab: o.tab === 'goal' || o.tab === 'toc' ? o.tab : fallback.tab,
      width: clampWidth(o.width),
      hintShown: o.hintShown === true,
      tocRole: o.tocRole === 'user' || o.tocRole === 'assistant' ? o.tocRole : 'all',
      tocRoundHeadsOnly: o.tocRoundHeadsOnly === true,
      tocSkipSystem: o.tocSkipSystem !== false,
    };
  } catch {
    return fallback;
  }
}

export const useUiStore = defineStore('ui', () => {
  const persisted = loadPersisted();

  /** Drawer 是否打开（不持久化：启动默认关闭，避免遮挡主界面） */
  const rightRailOpen = ref(false);
  const rightRailTab = ref<RightRailTab>(persisted.tab);
  const rightRailWidth = ref(clampWidth(persisted.width));
  const hintShown = ref(persisted.hintShown);

  /** 目录过滤 */
  const tocRole = ref<TocRoleFilter>(persisted.tocRole);
  const tocRoundHeadsOnly = ref(persisted.tocRoundHeadsOnly);
  const tocSkipSystem = ref(persisted.tocSkipSystem);

  /** 跳转锁：performance.now() 时间戳；期内禁止"新消息自动滚到底"（见 03 §4.5） */
  const jumpLockUntil = ref(0);
  const isJumpLocked = computed(() => performance.now() < jumpLockUntil.value);

  function open(tab?: RightRailTab): void {
    if (tab) rightRailTab.value = tab;
    rightRailOpen.value = true;
  }
  function close(): void { rightRailOpen.value = false; }
  function toggle(tab?: RightRailTab): void {
    if (rightRailOpen.value && (!tab || tab === rightRailTab.value)) { close(); return; }
    open(tab);
  }
  function setWidth(w: number): void { rightRailWidth.value = clampWidth(w); }
  function lockJump(ms = 1500): void { jumpLockUntil.value = performance.now() + ms; }
  function releaseJumpLock(): void { jumpLockUntil.value = 0; }
  function markHintShown(): void { hintShown.value = true; }

  // 节流持久化（100ms），避免拖拽时每帧写 localStorage
  let timer: number | null = null;
  watch(
    [rightRailTab, rightRailWidth, hintShown, tocRole, tocRoundHeadsOnly, tocSkipSystem],
    () => {
      if (timer !== null) window.clearTimeout(timer);
      timer = window.setTimeout(() => {
        timer = null;
        try {
          const data: Persisted = {
            tab: rightRailTab.value,
            width: rightRailWidth.value,
            hintShown: hintShown.value,
            tocRole: tocRole.value,
            tocRoundHeadsOnly: tocRoundHeadsOnly.value,
            tocSkipSystem: tocSkipSystem.value,
          };
          localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
        } catch { /* 隐私模式/配额满：仅本次会话内有效 */ }
      }, 100);
    }
  );

  return {
    rightRailOpen, rightRailTab, rightRailWidth, hintShown,
    tocRole, tocRoundHeadsOnly, tocSkipSystem,
    jumpLockUntil, isJumpLocked,
    open, close, toggle, setWidth, lockJump, releaseJumpLock, markHintShown,
  };
});
```

> ⚠️ `isJumpLocked` 是 **computed**，`performance.now()` 不是响应式源 → 它不会随时间自动变 false。
> 因此 `MessageTimeline` 里**不要**用 `ui.isJumpLocked`，而是**内联比较**：
> `if (performance.now() < ui.jumpLockUntil) return false;`（[03](./03-history-anchor-and-scroll.md) §4.5 已如此约定）。
> `isJumpLocked` 仅保留给"需要响应式的展示场景"（当前无），实现中可删。

## 2. `src/composables/useTimelineNav.ts`

```ts
/**
 * need19：时间轴锚点注册表 + 跳转（模块内 Map，非响应式）。
 * 使用方：WorkbenchView provide → MessageTimeline/SubHistoryView 注册 → HistoryTocPanel 跳转
 */
import { inject, provide, type InjectionKey } from 'vue';

export type JumpBehavior = 'auto' | 'smooth';
export interface ScrollToAnchorOptions {
  behavior?: JumpBehavior;
  highlight?: boolean;
  highlightMs?: number;
  paddingTop?: number;
  fallbackNearest?: boolean;
}

export interface TimelineNav {
  register(key: string, el: HTMLElement): void;
  unregister(key: string): void;
  clear(): void;
  setContainer(el: HTMLElement | null): void;
  setOrder(keys: string[]): void;
  scrollToAnchor(key: string, opts?: ScrollToAnchorOptions): boolean;
  scrollToBottom(): void;
  keys(): string[];
}

const FLASH_CLASS = 'timeline-anchor-flash';
const domIdOf = (key: string): string => `halo-msg-${key.replace(/[^A-Za-z0-9_-]/g, '_')}`;

export function createTimelineNav(): TimelineNav {
  const registry = new Map<string, HTMLElement>();
  let container: HTMLElement | null = null;
  let order: string[] = [];
  let activeEl: HTMLElement | null = null;
  let activeTimer: number | null = null;

  function highlight(el: HTMLElement, ms: number): void {
    if (activeEl && activeEl !== el) activeEl.classList.remove(FLASH_CLASS);
    if (activeTimer !== null) window.clearTimeout(activeTimer);
    activeEl = el;
    el.classList.add(FLASH_CLASS);
    activeTimer = window.setTimeout(() => {
      el.classList.remove(FLASH_CLASS);
      if (activeEl === el) activeEl = null;
      activeTimer = null;
    }, ms);
  }

  function resolveKey(key: string, fallbackNearest: boolean): { el: HTMLElement; key: string } | null {
    const hit = registry.get(key);
    if (hit) return { el: hit, key };
    if (!fallbackNearest || order.length === 0) return null;
    const i = order.indexOf(key);
    const start = i < 0 ? order.length - 1 : i;
    for (let j = start - 1; j >= 0; j--) {
      const el = registry.get(order[j]);
      if (el) return { el, key: order[j] };
    }
    for (let j = start + 1; j < order.length; j++) {
      const el = registry.get(order[j]);
      if (el) return { el, key: order[j] };
    }
    return null;
  }

  function scrollToAnchor(rawKey: string, opts: ScrollToAnchorOptions = {}): boolean {
    try {
      const paddingTop = opts.paddingTop ?? 12;
      const resolved = resolveKey(rawKey, opts.fallbackNearest !== false);
      if (!resolved) return false;
      const { el } = resolved;
      if (!container) {
        el.scrollIntoView({ block: 'center' });
      } else {
        const cRect = container.getBoundingClientRect();
        const eRect = el.getBoundingClientRect();
        const target = container.scrollTop + (eRect.top - cRect.top) - paddingTop;
        const max = Math.max(0, container.scrollHeight - container.clientHeight);
        container.scrollTo({ top: Math.max(0, Math.min(max, target)), behavior: opts.behavior ?? 'smooth' });
      }
      if (opts.highlight !== false) highlight(el, opts.highlightMs ?? 1200);
      return true;
    } catch (err) {
      console.warn('[timelineNav] scrollToAnchor failed:', err);
      return false;
    }
  }

  return {
    register(key, el) { if (!el) return; registry.set(key, el); if (!el.id) el.id = domIdOf(key); },
    unregister(key) { registry.delete(key); },
    clear() { registry.clear(); activeEl = null; },
    setContainer(el) { container = el; },
    setOrder(keys) { order = keys.slice(); },
    scrollToAnchor,
    scrollToBottom() { if (container) container.scrollTop = container.scrollHeight; },
    keys() { return order.slice(); },
  };
}

export const TIMELINE_NAV_KEY: InjectionKey<TimelineNav> = Symbol('halo.timelineNav');

export function provideTimelineNav(nav: TimelineNav): void { provide(TIMELINE_NAV_KEY, nav); }

/** 兜底：未 provide 时返回 no-op（避免单测/独立渲染崩溃） */
export function useTimelineNav(): TimelineNav {
  const nav = inject(TIMELINE_NAV_KEY, null);
  if (nav) return nav;
  const noop: TimelineNav = {
    register() {}, unregister() {}, clear() {}, setContainer() {}, setOrder() {},
    scrollToAnchor() { return false; }, scrollToBottom() {}, keys() { return []; },
  };
  console.warn('[timelineNav] not provided; using no-op');
  return noop;
}
```

**`setOrder(keys)` 的调用点**：`MessageTimeline` / `SubHistoryView` 在 `timelineItems` 变化时调用（`watch(..., { immediate: true })`），保证「向前/向后找最近锚点」的顺序依据最新。

## 3. `MessageTimeline` 改造（diff 级清单）

```diff
@@ 模板：三处条目根 div 增加函数式 ref
-        <div v-if="item.kind === 'user'" class="flex justify-end">
+        <div v-if="item.kind === 'user'" class="flex justify-end" :ref="setAnchor(item.key)">
...
-        <div v-else-if="item.kind === 'system'" class="text-[11px] text-secondary text-center font-mono px-4">
+        <div v-else-if="item.kind === 'system'" class="text-[11px] text-secondary text-center font-mono px-4" :ref="setAnchor(item.key)">
...
-        <div v-else class="space-y-2 min-w-0">
+        <div v-else class="space-y-2 min-w-0" :ref="setAnchor(item.key)">

@@ script
+import { useTimelineNav } from '@/composables/useTimelineNav';
+const nav = useTimelineNav();
+function setAnchor(key: string) {
+  return (el: Element | ComponentPublicInstance | null) => {
+    if (el instanceof HTMLElement) nav.register(key, el);
+    else nav.unregister(key);
+  };
+}
+watch(() => timelineItems.value.map((i) => i.key), (keys) => nav.setOrder(keys), { immediate: true });
+onMounted(() => { nav.setContainer(scrollContainerRef.value); });
+onUnmounted(() => { nav.clear(); nav.setContainer(null); });

@@ 滚底判定加入跳转锁
 function scrollTimelineToBottomIfNeeded(): boolean {
   const el = scrollContainerRef.value;
   if (!el) return false;
+  if (performance.now() < uiStore.jumpLockUntil) return false;   // need19 跳转锁
   const maxScroll = el.scrollHeight - el.clientHeight;
   ...
 }
+
+// 用户主动滚动 → 立即解锁（wheel/touch/keydown 才代表"人"）
+function onUserScrollIntent(): void { uiStore.releaseJumpLock(); }
+onMounted(() => {
+  const el = scrollContainerRef.value; if (!el) return;
+  el.addEventListener('wheel', onUserScrollIntent, { passive: true });
+  el.addEventListener('touchstart', onUserScrollIntent, { passive: true });
+  el.addEventListener('keydown', onUserScrollIntent);
+});
```

> `:ref="setAnchor(item.key)"` 注意是**调用**返回函数，不是直接绑函数本身；每次渲染会创建新函数 → Vue 会先卸载旧 ref（`null`）再挂新元素，正好触发 `unregister/register`，语义正确。

## 4. `src/utils/historyToc.ts`（核心逻辑）

```ts
/** need19：历史目录纯函数（无 Vue / 无 DOM 依赖，可裸 node 断言） */
import type { TimelineEntry } from '@/utils/turnGroup';

export type TocRole = 'user' | 'assistant' | 'system';
export interface TocItem {
  anchorKey: string; role: TocRole; label: string; fullText: string; charCount: number;
  index: number; round: number; toolCount: number; hasReasoning: boolean;
  createdAtUtc: string; isRoundHead: boolean;
}
export interface TocFilter { role: 'all' | 'user' | 'assistant'; roundHeadsOnly: boolean; skipSystem?: boolean }

const MAX_SCAN = 4000;          // 巨型消息只扫前 4k（E-07）
const LABEL_MAX = 15;

export function toDisplayText(raw: string | null | undefined): string { /* 见 05 §2.2 九步 */ }

export function bubbleLabel(text: string, max = LABEL_MAX): string { /* 见 05 §2.3 */ }

export function labelOfEntry(e: TimelineEntry): string {
  // user/system：正文；turn：content → toolCount → reasoning → 空
}

export function buildHistoryToc(entries: TimelineEntry[], filter: TocFilter): TocItem[] { /* 见 05 §2.5 */ }
```

## 5. `RightRailDrawer.vue` 关键片段（合成版）

```vue
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { Target, ListTree } from 'lucide-vue-next';
import { useUiStore } from '@/stores/uiStore';
import { useGoalStore } from '@/stores/goalStore';
import { useSubSessionStore } from '@/stores/subSessionStore';
import GoalPanelContent from '@/components/goal/GoalPanelContent.vue';
import HistoryTocPanel from './HistoryTocPanel.vue';
import PanelBoundary from './PanelBoundary.vue';

const props = defineProps<{ to?: string | HTMLElement }>();
const ui = useUiStore();
const goalStore = useGoalStore();
const sub = useSubSessionStore();
const rootRef = ref<HTMLElement | null>(null);

const drawerTo = computed(() => rootRef.value ?? props.to ?? 'body');
const tabs = computed(() => [
  { key: 'goal' as const, label: `目标${goalStore.pct ? ` · ${goalStore.pct}%` : ''}`, icon: Target, badge: '' },
  { key: 'toc' as const, label: '历史目录', icon: ListTree, badge: '' },
]);

onMounted(() => {
  goalStore.watchSession();                   // need19：唯一挂载点（幂等）
  if (ui.rightRailOpen) goalStore.load(/* ws/sess 由 store 内 watch 负责 */);
});

// 打开 Drawer 的 goal tab 时，把浮卡收成 mini，避免重复展示
watch([() => ui.rightRailOpen, () => ui.rightRailTab], ([open, tab]) => {
  if (open && tab === 'goal' && goalStore.panel === 'expanded') goalStore.setPanel('mini');
});
</script>
```

## 6. 关键决策速查（施工时别再犹豫）

| 决策点 | 结论 | 依据 |
|---|---|---|
| Drawer 挂在哪 | `WorkbenchView` 根节点 + `to` 绑定宿主 ref | [01](./01-architecture-design.md) §1 |
| 点外部不关 | `:mask-closable="false"` + `show-mask="transparent"` | [04](./04-drawer-spec-and-behavior.md) §2/§4 |
| 关闭按钮 | `n-drawer-content closable` | [04](./04-drawer-spec-and-behavior.md) §8 D4 |
| Esc | 允许关闭（一行可关） | 04 §6 Q1 |
| 是否占位排版 | 不占位（overlay） | [00](./00-README.md) §4.4 |
| 目录数据源 | `sessionMessages`（子会话用 `subSessionStore.messages`） | [01](./01-architecture-design.md) §4.1 |
| 锚点定位 | 注册表 + rect 差值 + `scrollTo` | [03](./03-history-anchor-and-scroll.md) §2 |
| 自动滚底冲突 | `jumpLockUntil` 1.5s + 用户滚动解锁 | 03 §4.5 |
| 气泡文本 | 清洗后前 15 码点 + `…` | [05](./05-quick-jump-panel.md) §2.3 |
| 长列表 | ≤400 全量；>400 分批 200 | 05 §4.2 |
| Goal 复用 | `GoalPanelContent`（mode=float/drawer） | [02](./02-goal-panel.md) §2 |
| `watchSession` | 迁到 `RightRailDrawer` 并幂等 | 02 §3 |
| 错误兜底 | 自实现 `PanelBoundary` | [07](./07-edge-cases.md) §8 |
