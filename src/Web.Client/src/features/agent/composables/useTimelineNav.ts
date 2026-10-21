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
        try { el.classList.remove(FLASH_CLASS); } catch { /* ignore */ }
        if (activeEl === el) activeEl = null;
        activeTimer = null;
      }, ms);
    } catch { /* ignore */ }
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
