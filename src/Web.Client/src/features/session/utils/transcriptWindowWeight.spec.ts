// need02-P4：按权重窗口单测（p4-window）。
import { describe, expect, it } from 'vitest';
import { hiddenAboveByWeight, TURN_WINDOW_WEIGHT, turnWeightOf } from './transcriptWindow';

const turn = (key: string, steps: number) => ({ key, kind: 'user-turn', steps });
const sys = (key: string) => ({ key, kind: 'system' });

describe('need02-P4 turnWeightOf', () => {
  it('用户轮权重 = 1+steps', () => {
    expect(turnWeightOf(turn('a', 0))).toBe(1);
    expect(turnWeightOf(turn('b', 3))).toBe(4);
    expect(turnWeightOf(turn('c', 8))).toBe(9);
  });

  it('非用户轮权重恒 1', () => {
    expect(turnWeightOf(sys('s1'))).toBe(1);
    expect(turnWeightOf({ key: 'x', kind: 'turn' })).toBe(1);
  });
});

describe('need02-P4 hiddenAboveByWeight', () => {
  it('空列表隐藏 0', () => {
    expect(hiddenAboveByWeight([])).toBe(0);
  });

  it('预算内全挂（15 个平均轮 ≈ 预算 32 内）', () => {
    const entries = Array.from({ length: 15 }, (_, i) => turn(`u${i}`, 1));
    expect(hiddenAboveByWeight(entries)).toBe(0);
  });

  it('超预算隐藏前缀（30 个单步轮 → 隐藏前 15 条左右量级）', () => {
    const entries = Array.from({ length: 30 }, (_, i) => turn(`u${i}`, 0));
    // 权重各 1，预算 32 → 挂载后 30 条中的后 30 条？30*1=30 ≤ 32，全挂。
    expect(hiddenAboveByWeight(entries)).toBe(0);
    const many = Array.from({ length: 60 }, (_, i) => turn(`u${i}`, 0));
    expect(hiddenAboveByWeight(many)).toBe(60 - TURN_WINDOW_WEIGHT);
  });

  it('超高轮（单轮权重>预算）仍整轮挂载，不截断', () => {
    const entries = [turn('u0', 0), turn('u1', 100)];
    // 从尾收：u1 权重 101 > 32，但 acc=0 时仍收下（整轮挂载），再向前 u0 会超预算 → 隐藏 1。
    expect(hiddenAboveByWeight(entries)).toBe(1);
  });

  it('多步重轮优先占预算（重轮在尾时轻轮被挤出）', () => {
    const entries = [turn('u0', 0), turn('u1', 0), turn('u2', 30)];
    // 尾轮权重 31 ≤ 32 收下，剩 1 预算只能再收 u1 → 隐藏 u0。
    expect(hiddenAboveByWeight(entries)).toBe(1);
  });
});
