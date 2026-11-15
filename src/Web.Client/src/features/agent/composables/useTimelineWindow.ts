import { computed, nextTick, ref } from 'vue';
import type { ComputedRef, Ref } from 'vue';
import type { ComponentPublicInstance } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import { createBottomPin, PIN_TOLERANCE_PX, useFollowMode } from './useTimelineNav';
import type { TimelineNav } from './useTimelineNav';
import { INITIAL_MOUNT, STEP, WINDOW_MIN, hiddenAboveByWeight, isNearTop } from '@features/session/utils/transcriptWindow';
import { isP4SubOn } from '@features/need02/utils/need02Flags';
import type { Need02Flags } from '@features/need02/utils/need02Flags';
import { useUiStore } from '@/stores/uiStore';
import { useAgentStream } from '@features/agent/stores/useAgentStream';

interface WindowDeps {
  timelineItems: ComputedRef<TimelineEntry[]> | Ref<TimelineEntry[]>;
  historyEntries: ComputedRef<TimelineEntry[]> | Ref<TimelineEntry[]>;
  need02Flags: Need02Flags;
  nav: TimelineNav;
}
/** 滚动与窗口：贴底跟随、阅读窗口、顶部扩窗、滚动绑定、锚点注册。 */
export function useTimelineWindow(deps: WindowDeps) {
  const uiStore = useUiStore();
  const stream = useAgentStream();
  const scrollEl = ref<HTMLElement | null>(null);
  const follow = useFollowMode();
  const pin = createBottomPin(() => scrollEl.value, {
    isLocked: () => performance.now() < uiStore.jumpLockUntil,
    release: () => uiStore.releaseJumpLock(),
    isFollowing: () => follow.pinnedRef.value === true,
  });
  const paneVisible = ref(true);
  const setPaneVisible = (v: boolean): void => {
    paneVisible.value = v;
  };
  const readingWindow = ref(false);
  const setReadingWindow = (v: boolean): void => {
    readingWindow.value = v;
  };
  const clearReadingWindow = (): void => {
    readingWindow.value = false;
  };
  const transcriptRunning = computed(() => stream.isGenerating && !readingWindow.value);
  const hiddenAbove = ref(0);
  const visibleHistoryEntries = computed<TimelineEntry[]>(() => deps.historyEntries.value.slice(hiddenAbove.value));
  function weightOfItem(e: TimelineEntry): number {
    try {
      const u = e as unknown as { kind?: string; turn?: { steps?: unknown[] } };
      if (u && u.kind === 'user-turn') return 1 + (Array.isArray(u.turn?.steps) ? (u.turn as { steps: unknown[] }).steps.length : 0);
    } catch (e) {
      console.warn('[timeline:window] weight read failed:', e);
    }
    return 1;
  }
  function resetMountWindow(): void {
    if (isP4SubOn('window', null, deps.need02Flags)) {
      hiddenAbove.value = hiddenAboveByWeight(
        deps.timelineItems.value.map((e) => ({
          key: e.key,
          kind: String((e as unknown as { kind?: string }).kind || ''),
          steps: weightOfItem(e) - 1,
        }))
      );
    } else hiddenAbove.value = Math.max(0, deps.timelineItems.value.length - INITIAL_MOUNT);
  }
  function ensureMountWindow(): void {
    resetMountWindow();
    requestAnimationFrame(() => {
      if (!follow.pinnedRef.value || !paneVisible.value) return;
      hiddenAbove.value = Math.max(0, deps.timelineItems.value.length - WINDOW_MIN);
      pin.forceToBottom();
    });
  }
  function growWindowTop(step = STEP): boolean {
    if (hiddenAbove.value <= 0) return false;
    if (isP4SubOn('window', null, deps.need02Flags)) {
      const items = deps.timelineItems.value;
      let acc = 0;
      let n = 0;
      for (let i = hiddenAbove.value - 1; i >= 0 && acc < step; i--) {
        acc += weightOfItem(items[i] as TimelineEntry);
        n++;
      }
      hiddenAbove.value = Math.max(0, hiddenAbove.value - Math.max(1, n));
      return true;
    }
    hiddenAbove.value = Math.max(0, hiddenAbove.value - step);
    return true;
  }
  function anchorPrepend(el: HTMLElement | null, beforeHeight: number): void {
    if (!el) return;
    try {
      const delta = el.scrollHeight - beforeHeight;
      if (delta > 0) el.scrollTop += delta;
    } catch (e) {
      console.warn('[timeline:window] anchor prepend failed:', e);
    }
  }
  async function handleTopReach(loadOlder: () => void | Promise<void>): Promise<void> {
    if (!paneVisible.value) return;
    if (growWindowTop()) return;
    const el = scrollEl.value;
    const before = el ? el.scrollHeight : 0;
    await loadOlder();
    await nextTick();
    anchorPrepend(el, before);
  }
  function expandTopWithAnchor(step = STEP): boolean {
    const el = scrollEl.value;
    const before = el ? el.scrollHeight : 0;
    if (!growWindowTop(step)) return false;
    void nextTick(() => {
      anchorPrepend(scrollEl.value, before);
    });
    return true;
  }
  const SCROLL_IDLE_MS = 120;
  const MAX_GROW_PER_REACH = 3;
  let scrollRaf = 0;
  let topGrowAt = 0;
  let growThisReach = 0;
  function onTranscriptScroll(): void {
    if (scrollRaf) return;
    scrollRaf = requestAnimationFrame(() => {
      scrollRaf = 0;
      const el = scrollEl.value;
      if (!el || !paneVisible.value) return;
      const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
      const pinnedNow = distanceFromBottom <= PIN_TOLERANCE_PX;
      if (pinnedNow !== follow.pinnedRef.value) {
        if (pinnedNow) {
          follow.ingest(distanceFromBottom, 'unknown');
          clearReadingWindow();
        } else follow.releaseFollow();
      }
      if (!isNearTop(el.scrollTop)) {
        growThisReach = 0;
        return;
      }
      if (growThisReach >= MAX_GROW_PER_REACH) return;
      const now = performance.now();
      if (now - topGrowAt < SCROLL_IDLE_MS) return;
      topGrowAt = now;
      if (expandTopWithAnchor()) growThisReach++;
    });
  }
  function bindScrollWindow(): void {
    const el = scrollEl.value;
    if (!el) return;
    el.addEventListener('scroll', onTranscriptScroll, { passive: true });
  }
  function unbindScrollWindow(): void {
    const el = scrollEl.value;
    if (!el) return;
    try {
      el.removeEventListener('scroll', onTranscriptScroll);
    } catch (e) {
      console.warn('[timeline:window] unbind scroll failed:', e);
    }
    if (scrollRaf) cancelAnimationFrame(scrollRaf);
    scrollRaf = 0;
  }
  function setAnchor(key: string) {
    return (el: Element | ComponentPublicInstance | null) => {
      try {
        if (el instanceof HTMLElement) deps.nav.register(key, el);
        else deps.nav.unregister(key);
      } catch (e) {
        console.warn('[timeline:window] anchor failed:', e);
      }
    };
  }
  return {
    scrollEl,
    follow,
    pin,
    paneVisible,
    setPaneVisible,
    readingWindow,
    setReadingWindow,
    clearReadingWindow,
    transcriptRunning,
    hiddenAbove,
    visibleHistoryEntries,
    resetMountWindow,
    ensureMountWindow,
    growWindowTop,
    anchorPrepend,
    handleTopReach,
    expandTopWithAnchor,
    onTranscriptScroll,
    bindScrollWindow,
    unbindScrollWindow,
    setAnchor,
  };
}
export type TimelineWindow = ReturnType<typeof useTimelineWindow>;
