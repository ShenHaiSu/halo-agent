/**
 * 回归：历史窗口「向上扩窗」链路。
 *
 * 背景（本次修复的 bug）：`useTimeline` 里 `growWindowTop` / `handleTopReach` 早已实现，
 * 但**没有任何事件源**——滚动容器上没有 `scroll` 监听，`bindUserScroll` 只挂了
 * wheel/touchstart/keydown。结果 `hiddenAbove` 在会话打开后永久固定
 * （15 或 60 条），用户滚到顶部也看不到更早的历史，且没有任何报错，属于静默失效。
 *
 * 修复：`useTimeline.bindScrollWindow()` 在 attachContainer 时注册 scroll 监听，
 * 经 rAF 合帧后调用 `isNearTop()` 判定是否扩窗。
 * 这里锁住纯函数契约 + 扩窗状态机，避免再次退化成死代码。
 */
import { describe, expect, it } from 'vitest';
import {
  INITIAL_MOUNT,
  isNearTop,
  NEAR_TOP_PX,
  STEP,
  WINDOW_MIN,
  growWindow,
  initialWindow,
  fullWindow,
  expandToMin,
  sliceWindow,
} from '@features/session/utils/transcriptWindow';

describe('近顶判定（isNearTop）', () => {
  it('阈值内才算近顶', () => {
    expect(isNearTop(0)).toBe(true);
    expect(isNearTop(NEAR_TOP_PX)).toBe(true); // 边界包含
    expect(isNearTop(NEAR_TOP_PX + 1)).toBe(false);
  });

  it('负 scrollTop（Safari 橡皮筋回弹）按 0 处理', () => {
    expect(isNearTop(-80)).toBe(true);
  });

  it('自定义阈值可覆盖', () => {
    expect(isNearTop(100, 50)).toBe(false);
    expect(isNearTop(50, 50)).toBe(true);
  });

  it('阈值必须明显大于单步扩窗量，否则一进来就白扩', () => {
    expect(NEAR_TOP_PX).toBeGreaterThan(STEP);
  });
});

describe('近顶 → 扩窗的联动（模拟 scroll 回调里的判定顺序）', () => {
  /** 与 useTimeline 的 MAX_GROW_PER_REACH 保持一致（同步常量，改一处必须改两处，故加断言）。 */
  const MAX_GROW_PER_REACH = 3;

  /**
   * 复刻 useTimeline.onTranscriptScroll 的核心分支：
   * 「近顶 && 冷却已过 && 本轮未超轮数上限」才扩窗。
   *
   * 这里故意用「条数」当高度代理（每轮只撑高 STEP=40px < NEAR_TOP_PX=240），
   * 复现消息很短时的最坏情况——此时锚定补偿不足以把 scrollTop 抬出近顶区，
   * 必须靠 MAX_GROW_PER_REACH 兜底，否则会在一瞬间扩到全量。
   */
  function simulate(stepPxPerEntry: number, frames = 40) {
    let scrollTop = 0;
    let w = { start: 140, end: 200, total: 200 };
    const rounds: number[] = [];
    let growThisReach = 0;
    for (let i = 0; i < frames; i++) {
      if (!isNearTop(scrollTop)) {
        growThisReach = 0;
        break;
      }
      if (growThisReach >= MAX_GROW_PER_REACH) break;
      const before = w.end - w.start;
      const next = growWindow(w);
      if (next.grown === 0) break; // 已覆盖全量
      w = next.window;
      rounds.push(w.end - w.start - before);
      growThisReach++;
      scrollTop += next.grown * stepPxPerEntry; // anchorPrepend: scrollTop += scrollHeight 增量
    }
    return { rounds, scrollTop, window: w };
  }

  it('短消息（每轮撑高不足阈值）也严格被轮数上限截断，不会一瞬间扩到全量', () => {
    const r = simulate(1); // 1px/条 → 每轮只撑高 STEP px，远小于 NEAR_TOP_PX
    expect(r.rounds).toHaveLength(MAX_GROW_PER_REACH);
    expect(r.rounds.every((n) => n === STEP)).toBe(true);
    // 关键反例：不能因为短消息就一路扩到 start=0
    expect(r.window.start).toBeGreaterThan(0);
  });

  it('高消息（锚定足以把 scrollTop 抬出近顶区）时提前停止，无需用满上限', () => {
    const r = simulate(10); // 10px/条 → 每轮撑高 400px > NEAR_TOP_PX
    expect(r.rounds.length).toBeLessThan(MAX_GROW_PER_REACH);
    expect(isNearTop(r.scrollTop)).toBe(false);
  });

  it('已覆盖全量时 growWindow 返回 grown=0，扩窗自然终止', () => {
    const w = { start: 0, end: 200, total: 200 };
    expect(growWindow(w).grown).toBe(0);
    expect(isNearTop(0)).toBe(true); // 仍在近顶区，但已无可扩 → 由 grown=0 兜底
  });
});

describe('窗口初值（打开会话时只挂尾部）', () => {
  it('长会话初始只挂 INITIAL_MOUNT 条', () => {
    const w = initialWindow(200);
    expect(w.end - w.start).toBe(INITIAL_MOUNT);
    expect(w.end).toBe(200);
    expect(
      sliceWindow(
        Array.from({ length: 200 }, (_, i) => i),
        w
      )
    ).toHaveLength(INITIAL_MOUNT);
  });

  it('短会话不足 INITIAL_MOUNT 时全量（start 钳到 0）', () => {
    const w = initialWindow(4);
    expect(w.start).toBe(0);
    expect(w.end - w.start).toBe(4);
  });
});

describe('向上扩窗（growWindow）', () => {
  it('每次只向上扩 STEP 条，end 不动', () => {
    const w = initialWindow(200);
    const r = growWindow(w);
    expect(r.grown).toBe(STEP);
    expect(r.window.start).toBe(Math.max(0, w.start - STEP));
    expect(r.window.end).toBe(200);
  });

  it('到顶后 grown=0，不会越界成负数', () => {
    const r = growWindow({ start: 0, end: 200, total: 200 });
    expect(r.grown).toBe(0);
    expect(r.window.start).toBe(0);
  });

  it('从 15 逐次扩到覆盖全量：这是用户滚到顶应看到的效果', () => {
    let w = initialWindow(200);
    let rounds = 0;
    while (w.start > 0 && rounds < 100) {
      w = growWindow(w).window;
      rounds++;
    }
    expect(w.start).toBe(0); // 最终能看到第 1 条
    expect(
      sliceWindow(
        Array.from({ length: 200 }, (_, i) => i),
        w
      )
    ).toHaveLength(200);
    expect(rounds).toBeGreaterThan(0);
  });

  it('expandToMin 只在低于 WINDOW_MIN 时才补到 60', () => {
    expect(expandToMin({ start: 185, end: 200, total: 200 }).start).toBe(200 - WINDOW_MIN);
    // 已经 >= 60 条则原样返回（引用相等，避免无谓重渲染）
    const big = { start: 0, end: 200, total: 200 };
    expect(expandToMin(big)).toBe(big);
  });

  it('fullWindow 覆盖全部', () => {
    const w = fullWindow(200);
    expect(w.start).toBe(0);
    expect(w.end).toBe(200);
  });
});
