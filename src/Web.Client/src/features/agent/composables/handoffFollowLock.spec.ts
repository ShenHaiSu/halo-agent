/** 交接锁：几何变化不解除跟随；用户手势立即让出控制权。 */
import { describe, expect, it } from 'vitest';
import { computed } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { useTimelineWindow } from './useTimelineWindow';
import { useUiStore } from '@/stores/uiStore';
import type { TimelineNav } from './useTimelineNav';

const noopNav = {
  register() {},
  unregister() {},
  clear() {},
  setContainer() {},
  setOrder() {},
  async scrollToAnchor() {
    return false;
  },
  scrollToBottom() {},
  keys() {
    return [];
  },
  navEpoch() {
    return 0;
  },
} as TimelineNav;

function makeWindow() {
  setActivePinia(createPinia());
  const uiStore = useUiStore();
  const win = useTimelineWindow({
    timelineItems: computed(() => []),
    historyEntries: computed(() => []),
    need02Flags: { p1: true, p3: true, p4: true },
    nav: noopNav,
  });
  // scrollTop=500 → 距底 100px > 24px 容差，几何上会被判为「脱离」。
  const el = {
    scrollHeight: 1000,
    clientHeight: 400,
    scrollTop: 500,
    addEventListener() {},
    removeEventListener() {},
  };
  win.scrollEl.value = el as unknown as HTMLElement;
  win.setPaneVisible(true);
  return { uiStore, win };
}

async function waitScrollFrame(): Promise<void> {
  await new Promise((r) => setTimeout(r, 40));
}

describe('交接期滚动锁', () => {
  it('锁期内几何变化不调 releaseFollow，pinnedRef 保持 true', async () => {
    const { uiStore, win } = makeWindow();
    expect(win.follow.pinnedRef.value).toBe(true);
    uiStore.lockHandoff();
    expect(uiStore.handoffLocked).toBe(true);

    win.onTranscriptScroll();
    await waitScrollFrame();
    expect(win.follow.pinnedRef.value).toBe(true);
    expect(win.follow.showJump.value).toBe(false);
  });

  it('用户手势清交接锁后，几何变化仍然可以解除跟随', async () => {
    const { uiStore, win } = makeWindow();
    uiStore.lockHandoff();
    win.onTranscriptScroll();
    await waitScrollFrame();
    expect(win.follow.pinnedRef.value).toBe(true);

    // 手势路径（wheel/touch/keydown）最终落到 releaseJumpLock。
    uiStore.releaseJumpLock();
    expect(uiStore.handoffLocked).toBe(false);
    win.onTranscriptScroll();
    await waitScrollFrame();
    expect(win.follow.pinnedRef.value).toBe(false);
    expect(win.follow.showJump.value).toBe(true);
  });

  it('锁只禁止自动解除跟随，不禁止用户主动滚动（scrollTop 不被强制改写）', async () => {
    const { uiStore, win } = makeWindow();
    const el = win.scrollEl.value as unknown as { scrollTop: number };
    uiStore.lockHandoff();
    win.onTranscriptScroll();
    await waitScrollFrame();
    expect(el.scrollTop).toBe(500);
  });
});
