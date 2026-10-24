/** M2 滚动跟随 + 流式平滑（§2.1 自动化断言）。 */
import { describe, expect, it } from 'vitest';
import { createBottomPin, nextScrollTop, PIN_TOLERANCE_PX } from '@features/agent/composables/useTimelineNav';
import { computeSmoothStep, SMOOTH_MAX_BACKLOG } from '@features/agent/composables/useSmoothText';

describe('跟随位移（M2-1 nextScrollTop）', () => {
  it('小增量走渐进：单帧推进约 35%', () => {
    expect(nextScrollTop(1000, 1100, 600, false)).toBe(1035);
  });

  it('大跳瞬移（超过 0.6 × 视口高）', () => {
    expect(nextScrollTop(0, 2000, 600, false)).toBe(2000);
  });

  it('force 始终瞬移', () => {
    expect(nextScrollTop(1000, 1010, 600, true)).toBe(1010);
  });

  it('极小增量不会越过目标（2px 下限只用来避免取整后原地踏步）', () => {
    // delta=1 时 min(delta, max(2, 1)) = 1 —— 直接到位，不会 overshoot。
    expect(nextScrollTop(1000, 1001, 600, false)).toBe(1001);
    // delta=3 时步长上限 2px
    expect(nextScrollTop(1000, 1003, 600, false)).toBe(1002);
  });
});

describe('贴底阈值（M2-2）', () => {
  function fakeEl(scrollTop: number) {
    return { scrollHeight: 1000, clientHeight: 400, scrollTop, addEventListener() {}, removeEventListener() {} };
  }
  function makePin(el: ReturnType<typeof fakeEl>, following = true) {
    return createBottomPin(() => el as unknown as HTMLElement, {
      isLocked: () => false,
      release: () => undefined,
      isFollowing: () => following,
    });
  }

  it('pinned 判定使用统一阈值 24px', () => {
    expect(PIN_TOLERANCE_PX).toBe(24);
    // 距离 = 1000 - 576 - 400 = 24 → 仍视为贴底
    expect(makePin(fakeEl(576)).isPinnedToBottom()).toBe(true);
    // 距离 30 → 已脱离
    expect(makePin(fakeEl(570)).isPinnedToBottom()).toBe(false);
  });

  it('用户已上翻（isFollowing=false）时非强制路径不再拽回底部', () => {
    const el = fakeEl(576);
    const pin = makePin(el, false);
    expect(pin.scrollIfNeeded()).toBe(false);
    expect(el.scrollTop).toBe(576);
  });

  it('贴底且跟随中：渐进推进而不是一帧到位', () => {
    const el = fakeEl(590); // maxScroll=600 → 剩余 10px
    const pin = makePin(el, true);
    expect(pin.scrollIfNeeded()).toBe(true);
    expect(el.scrollTop).toBe(594); // 590 + ceil(10*0.35)=4
    expect(el.scrollTop).toBeLessThan(600);
  });

  it('force 路径仍然瞬移到底', () => {
    const el = fakeEl(590);
    makePin(el, false).scrollIfNeeded(true);
    expect(el.scrollTop).toBe(600);
  });
});

describe('useSmoothText 步进（M2-4）', () => {
  it('backlog 小于追赶窗口时按 60ch/s 匀速推进', () => {
    const r = computeSmoothStep(0, 20, 1 / 60, 0);
    expect(r.take).toBe(1);
  });

  it('backlog 大时按 0.5s 追平（不会越流越慢）', () => {
    // backlog = 300 > 60*0.5=30 → speed = 300/0.5 = 600ch/s → 单帧 1/60s ≈ 10ch
    const r = computeSmoothStep(0, 300, 1 / 60, 0);
    expect(r.take).toBeGreaterThan(1);
    expect(r.carry).toBeLessThan(1);
  });

  it('已追平则不再推进', () => {
    expect(computeSmoothStep(50, 50, 1 / 60, 0.9)).toEqual({ take: 0, carry: 0 });
  });

  it('落后上限常量为 400（超出由组件快进，防止永远落后）', () => {
    expect(SMOOTH_MAX_BACKLOG).toBe(400);
  });
});
