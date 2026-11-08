/** P03 新增 7 例：leaveMs/minCover/imgPending/回退/dispose（09 §9.1 G6-1 分支清单）。控制器单测禁 DOM。 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createSettleController, LEAVE_MS } from './transcriptSettle';

describe('P03 settle 控制器增量语义', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('leaveMs 透传：180ms 前仍 leaving，180ms 后 off', () => {
    const ctrl = createSettleController({ leaveMs: 180 });
    const s = { scrollHeight: 5, clientHeight: 5 };
    for (let i = 0; i < 3; i++) ctrl.sample(s);
    expect(ctrl.phase).toBe('leaving');
    expect(ctrl.veilLeaving).toBe(true);
    vi.advanceTimersByTime(179);
    expect(ctrl.phase).toBe('leaving');
    vi.advanceTimersByTime(1);
    expect(ctrl.phase).toBe('off');
    expect(ctrl.veilCovering).toBe(false);
    expect(ctrl.veilLeaving).toBe(false);
  });

  it('leaveMs 解析失败回退 LEAVE_MS：不传即 180', () => {
    const ctrl = createSettleController();
    const s = { scrollHeight: 5, clientHeight: 5 };
    for (let i = 0; i < 3; i++) ctrl.sample(s);
    vi.advanceTimersByTime(LEAVE_MS - 1);
    expect(ctrl.phase).toBe('leaving');
    vi.advanceTimersByTime(1);
    expect(ctrl.phase).toBe('off');
  });

  it('minCover 边界：过早 finish 推迟到 120ms 才进 leaving', () => {
    const ctrl = createSettleController({ minCoverMs: 120 });
    const s = { scrollHeight: 7, clientHeight: 7 };
    for (let i = 0; i < 3; i++) ctrl.sample(s);
    // 距 startedAt 几乎 0ms，minCover 门应推迟 leaving
    expect(ctrl.phase).toBe('covering');
    vi.advanceTimersByTime(120);
    expect(ctrl.phase).toBe('leaving');
    vi.advanceTimersByTime(LEAVE_MS);
    expect(ctrl.phase).toBe('off');
  });

  it('shortSession 无视 minCover：直接 off', () => {
    const ctrl = createSettleController({ shortSession: true, minCoverMs: 120 });
    expect(ctrl.phase).toBe('off');
    expect(ctrl.veilCovering).toBe(false);
    vi.advanceTimersByTime(1000);
    expect(ctrl.phase).toBe('off');
  });

  it('dispose 后推进 1000ms 无任何 onPhase', () => {
    const ctrl = createSettleController();
    const phases: string[] = [];
    ctrl.onPhase = (p) => phases.push(p);
    ctrl.sample({ scrollHeight: 1, clientHeight: 1 });
    ctrl.dispose();
    vi.advanceTimersByTime(1000);
    expect(phases).toEqual([]);
    expect(ctrl.veilCovering).toBe(false);
  });

  it('imgPending 门：有未打点图片时 3 帧稳定也不收口，600ms 兜底仍放行', () => {
    const ctrl = createSettleController();
    const s = { scrollHeight: 9, clientHeight: 9, imgPending: 2 };
    for (let i = 0; i < 5; i++) ctrl.sample(s);
    expect(ctrl.phase).toBe('covering');
    vi.advanceTimersByTime(600);
    expect(ctrl.phase).toBe('leaving');
  });

  it('imgPending 清零后正常收口', () => {
    const ctrl = createSettleController();
    ctrl.sample({ scrollHeight: 9, clientHeight: 9, imgPending: 1 });
    ctrl.sample({ scrollHeight: 9, clientHeight: 9, imgPending: 1 });
    expect(ctrl.phase).toBe('covering');
    const s = { scrollHeight: 9, clientHeight: 9, imgPending: 0 };
    for (let i = 0; i < 3; i++) ctrl.sample(s);
    expect(ctrl.phase).toBe('leaving');
  });

  it('leaving 回退仅一次 + off 恰好一次', () => {
    const ctrl = createSettleController({ leaveMs: 10000 });
    const phases: string[] = [];
    ctrl.onPhase = (p) => phases.push(p);
    const s = { scrollHeight: 3, clientHeight: 3 };
    for (let i = 0; i < 3; i++) ctrl.sample(s);
    expect(phases).toEqual(['leaving']);
    ctrl.revertToCovering();
    expect(phases).toEqual(['leaving', 'covering']);
    // 第二次回退被拒绝（仅一次）
    ctrl.revertToCovering();
    expect(phases).toEqual(['leaving', 'covering']);
    // 回退后重进 leaving，再正常 off
    for (let i = 0; i < 3; i++) ctrl.sample(s);
    expect(phases).toEqual(['leaving', 'covering', 'leaving']);
    vi.advanceTimersByTime(10000);
    expect(phases).toEqual(['leaving', 'covering', 'leaving', 'off']);
    expect(phases.filter((p) => p === 'off')).toHaveLength(1);
  });
});
