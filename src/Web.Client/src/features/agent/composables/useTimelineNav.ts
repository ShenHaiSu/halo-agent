/**
 * need19：时间轴锚点注册表 + 跳转（模块内 Map，非响应式）。
 * 使用方：WorkbenchView provide → MessageTimeline/SubHistoryView 注册 → HistoryTocPanel 跳转
 */
import { inject, provide, ref, type InjectionKey } from 'vue';
import { reduceTranscriptScroll, type ScrollGesture, type ScrollState } from '@features/session/utils/transcriptScroll';

export type JumpBehavior = 'auto' | 'smooth';
export interface ScrollToAnchorOptions {
  behavior?: JumpBehavior;
  highlight?: boolean;
  highlightMs?: number;
  paddingTop?: number;
  /** 锚点缺失时的兜底：按索引找最近的更早锚点；默认 true */
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
    try {
      if (activeEl && activeEl !== el) activeEl.classList.remove(FLASH_CLASS);
      if (activeTimer !== null) window.clearTimeout(activeTimer);
      activeEl = el;
      el.classList.add(FLASH_CLASS);
      activeTimer = window.setTimeout(() => {
        try {
          el.classList.remove(FLASH_CLASS);
        } catch {
          /* ignore */
        }
        if (activeEl === el) activeEl = null;
        activeTimer = null;
      }, ms);
    } catch {
      /* ignore */
    }
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
    register(key, el) {
      if (!el) return;
      registry.set(key, el);
      if (!el.id) el.id = domIdOf(key);
    },
    unregister(key) {
      registry.delete(key);
    },
    clear() {
      registry.clear();
      activeEl = null;
    },
    setContainer(el) {
      container = el;
    },
    setOrder(keys) {
      order = keys.slice();
    },
    scrollToAnchor,
    scrollToBottom() {
      if (container) container.scrollTop = container.scrollHeight;
    },
    keys() {
      return order.slice();
    },
  };
}

export const TIMELINE_NAV_KEY: InjectionKey<TimelineNav> = Symbol('halo.timelineNav');

export function provideTimelineNav(nav: TimelineNav): void {
  provide(TIMELINE_NAV_KEY, nav);
}

/** 兜底：未 provide 时返回 no-op（避免单测/独立渲染崩溃） */
export function useTimelineNav(): TimelineNav {
  const nav = inject(TIMELINE_NAV_KEY, null);
  if (nav) return nav;
  const noop: TimelineNav = {
    register() {},
    unregister() {},
    clear() {},
    setContainer() {},
    setOrder() {},
    scrollToAnchor() {
      return false;
    },
    scrollToBottom() {},
    keys() {
      return [];
    },
  };
  console.warn('[timelineNav] not provided; using no-op');
  return noop;
}
export interface BottomPinOpts {
  isLocked(): boolean;
  release(): void;
  /**
   * M2-2：用户是否处于「跟随」意图（false = 用户主动上翻）。
   * `pinnedRef` 是意图的真相源，几何距离只是它的近似——两者冲突时以意图为准。
   */
  isFollowing?(): boolean;
}
/**
 * M2-2：视为「贴底」的容差。
 * 需与 transcriptScroll 的手势判定（1px 上滚即解 pinned）保持同一量级，
 * 否则会出现「手势判为脱离、几何判为贴底」→ 用户上翻后又被拽回底部（R4 第二隐患）。
 */
export const PIN_TOLERANCE_PX = 24;
/** M2-1：单帧追上剩余距离的比例；配合 2px 下限，约 6~8 帧收敛。 */
export const FOLLOW_EASE_RATIO = 0.35;
/**
 * M2-1：计算下一帧的 scrollTop（纯函数，便于单测）。
 *
 * 分级位移：
 *  · force（发送/跳转/切会话）与大增量（> max(48, 0.6 × clientHeight)）→ 瞬移；
 *  · 流式跟随的小增量 → 渐进逼近，避免「每帧硬赋值」造成的顿挫。
 */
export function nextScrollTop(current: number, target: number, clientHeight: number, force: boolean): number {
  const delta = target - current;
  if (delta <= 0) return current;
  if (force || delta > Math.max(48, clientHeight * 0.6)) return target;
  // 位移越小越缓：最大步长 = ceil(delta * 0.35)，下限 2px 避免永远差 1~2px 追不上
  return current + Math.min(delta, Math.max(2, Math.ceil(delta * FOLLOW_EASE_RATIO)));
}
export interface BottomPin {
  forceToBottom(): void;
  scheduleToBottom(): void;
  cancelScheduled(): void;
  isPinnedToBottom(): boolean;
  scrollIfNeeded(force?: boolean): boolean;
  bindUserScroll(): void;
  unbindUserScroll(): void;
}
export function createBottomPin(getEl: () => HTMLElement | null, opts: BottomPinOpts): BottomPin {
  let raf = 0;
  let timer: number | null = null;
  function cancelScheduled(): void {
    if (raf) cancelAnimationFrame(raf);
    if (timer !== null) window.clearTimeout(timer);
    raf = 0;
    timer = null;
  }
  function isPinnedToBottom(): boolean {
    const el = getEl();
    if (!el) return true;
    try {
      return el.scrollHeight - el.scrollTop - el.clientHeight <= PIN_TOLERANCE_PX;
    } catch (e) {
      console.warn('[timeline] pinned check failed:', e);
      return true;
    }
  }
  function scrollIfNeeded(force?: boolean): boolean {
    const el = getEl();
    if (!el) return false;
    try {
      if (opts.isLocked()) return false;
    } catch (e) {
      console.warn('[timeline] jump lock check failed:', e);
    }
    // M2-2：非强制路径额外尊重「跟随意图」，避免几何距离把上翻的用户拽回底部。
    if (!force) {
      try {
        if (opts.isFollowing && !opts.isFollowing()) return false;
      } catch (e) {
        console.warn('[timeline] follow check failed:', e);
      }
      if (!isPinnedToBottom()) return false;
    }
    const maxScroll = el.scrollHeight - el.clientHeight;
    if (maxScroll <= 0) return false;
    // 剩余距离 = 目标（maxScroll） - 当前 scrollTop。不能用 scrollHeight 直接相减：
    // 那会把 clientHeight 也算进 delta，导致「贴着底也被判成大跳」→ 渐进分支永远走不到。
    const delta = maxScroll - el.scrollTop;
    if (!force && delta <= 1.5) return false;
    el.scrollTop = nextScrollTop(el.scrollTop, maxScroll, el.clientHeight, !!force);
    return true;
  }
  function forceToBottom(): void {
    scrollIfNeeded(true);
  }
  function scheduleToBottom(): void {
    if (raf) cancelAnimationFrame(raf);
    raf = requestAnimationFrame(() => {
      raf = requestAnimationFrame(() => {
        raf = 0;
        scrollIfNeeded();
      });
    });
    if (timer !== null) window.clearTimeout(timer);
    timer = window.setTimeout(() => {
      timer = null;
      scrollIfNeeded();
    }, 200);
  }
  function onUserScrollIntent(): void {
    try {
      opts.release();
    } catch (e) {
      console.warn('[timeline] unlock failed:', e);
    }
  }
  function bindUserScroll(): void {
    const el = getEl();
    if (!el) return;
    el.addEventListener('wheel', onUserScrollIntent, { passive: true });
    el.addEventListener('touchstart', onUserScrollIntent, { passive: true });
    el.addEventListener('keydown', onUserScrollIntent);
  }
  function unbindUserScroll(): void {
    const el = getEl();
    if (!el) return;
    try {
      el.removeEventListener('wheel', onUserScrollIntent);
      el.removeEventListener('touchstart', onUserScrollIntent);
      el.removeEventListener('keydown', onUserScrollIntent);
    } catch (e) {
      console.warn('[timeline] unbind failed:', e);
    }
  }
  return { forceToBottom, scheduleToBottom, cancelScheduled, isPinnedToBottom, scrollIfNeeded, bindUserScroll, unbindUserScroll };
}
/** bigChange P2-3: pinned/showJump dual-mode state machine (reducer-backed). */
export interface FollowState {
  pinned: boolean;
  showJump: boolean;
}
export function useFollowMode(initial?: Partial<ScrollState>): {
  pinnedRef: ReturnType<typeof ref<boolean>>;
  showJump: ReturnType<typeof ref<boolean>>;
  releaseFollow: () => void;
  jumpToLatest: (scrollToBottom: () => void, onReturnToLatest?: () => void) => void;
  onSendRisingEdge: (scrollToBottom: () => void) => void;
  ingest: (distanceFromBottom: number, gesture: ScrollGesture) => void;
} {
  const pinnedRef = ref(initial?.pinned ?? true);
  const showJump = ref(initial?.showJump ?? !pinnedRef.value);
  function apply(s: ScrollState): void {
    pinnedRef.value = s.pinned;
    showJump.value = s.showJump;
  }
  function ingest(distanceFromBottom: number, gesture: ScrollGesture): void {
    apply(reduceTranscriptScroll(distanceFromBottom, gesture, { pinned: pinnedRef.value, showJump: showJump.value }));
  }
  function releaseFollow(): void {
    pinnedRef.value = false;
    showJump.value = true;
  }
  function jumpToLatest(scrollToBottom: () => void, onReturnToLatest?: () => void): void {
    pinnedRef.value = true;
    showJump.value = false;
    try {
      onReturnToLatest?.();
    } catch {
      /* ignore */
    }
    try {
      scrollToBottom();
    } catch (e) {
      console.warn('[timelineNav] jumpToLatest failed:', e);
    }
  }
  function onSendRisingEdge(scrollToBottom: () => void): void {
    pinnedRef.value = true;
    showJump.value = false;
    try {
      scrollToBottom();
    } catch (e) {
      console.warn('[timelineNav] send edge failed:', e);
    }
  }
  return { pinnedRef, showJump, releaseFollow, jumpToLatest, onSendRisingEdge, ingest };
}
