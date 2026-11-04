import { beforeEach, describe, expect, it, vi, afterEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useStreamStore } from './stream';

beforeEach(() => {
  setActivePinia(createPinia());
});

afterEach(() => {
  vi.useRealTimers();
});

describe('P24 stream.cancel 取消态', () => {
  it('F2: beginCancel 置 cancelling，endTurn/endCancel 复位', () => {
    const s = useStreamStore();
    s.beginTurn('t1');
    s.beginCancel();
    expect(s.cancelling).toBe(true);
    expect(s.cancelRequestedAt).toBeGreaterThan(0);
    expect(s.cancelTimedOut).toBe(false);

    s.endCancel();
    expect(s.cancelling).toBe(false);
    expect(s.cancelTimedOut).toBe(false);
  });

  it('F2b: beginTurn 会清掉上一轮的 cancelling（防新回合被旧取消态污染）', () => {
    const s = useStreamStore();
    s.beginTurn('t1');
    s.beginCancel();
    expect(s.cancelling).toBe(true);

    s.beginTurn('t2');
    expect(s.cancelling).toBe(false);
    expect(s.cancelTimedOut).toBe(false);
    expect(s.currentTurn?.turnId).toBe('t2');
  });

  it('F2c: 静默期超时只退出 busy，保留 currentTurn', () => {
    vi.useFakeTimers();
    const s = useStreamStore();
    s.beginTurn('t1');
    s.beginCancel();
    expect(s.cancelling).toBe(true);

    vi.advanceTimersByTime(2001);
    expect(s.cancelling).toBe(false);
    expect(s.busy).toBe(false);
    expect(s.currentTurn).not.toBeNull();        // ★ O4：不能清，否则迟到帧变孤儿
    expect(s.currentTurn?.turnId).toBe('t1');
    expect(s.cancelTimedOut).toBe(true);
    vi.useRealTimers();
  });

  it('F2c-反例: endTurn 会清 currentTurn（与兜底行为对比，防回退）', () => {
    const s = useStreamStore();
    s.beginTurn('t1');
    s.endTurn();
    expect(s.currentTurn).toBeNull();
  });
});
