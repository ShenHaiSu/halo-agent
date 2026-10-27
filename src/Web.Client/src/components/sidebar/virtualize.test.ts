// src/Web.Client/src/components/sidebar/virtualize.test.ts
import { describe, expect, it } from 'vitest';
import { buildOffsets, computeVisible, findStartRow } from './virtualize';

const rows = [
  { h: 28 }, // header
  ...Array.from({ length: 10 }, () => ({ h: 56 })),
  { h: 28 },
  ...Array.from({ length: 10 }, () => ({ h: 56 })),
];

describe('virtualize', () => {
  it('offsets 精确累加', () => {
    const off = buildOffsets([{ h: 56 }, { h: 28 }, { h: 56 }]);
    expect(off).toEqual([0, 56, 84, 140]);
  });

  it('① 顶端 scrollTop=0 → start=0', () => {
    const off = buildOffsets(rows);
    const v = computeVisible(off, 0, 400, 5);
    expect(v.start).toBe(0);
    expect(v.padTop).toBe(0);
  });

  it('② 底部 scrollTop=max → end=rows.length', () => {
    const off = buildOffsets(rows);
    const total = off[off.length - 1]!;
    const v = computeVisible(off, total - 400, 400, 5);
    expect(v.end).toBe(rows.length);
  });

  it('③ 单行比视口高', () => {
    const off = buildOffsets([{ h: 2000 }]);
    const v = computeVisible(off, 500, 300, 0);
    expect(v.start).toBe(0);
    expect(v.end).toBe(1);
  });

  it('findStartRow 单调性（1000 次随机 y）', () => {
    const off = buildOffsets(rows);
    for (let k = 0; k < 1000; k++) {
      const y = Math.random() * off[off.length - 1]!;
      const r = findStartRow(off, y);
      expect(r).toBeGreaterThanOrEqual(0);
      expect(r).toBeLessThan(rows.length);
    }
  });

  it('findStartRow 返回值一定包含 y', () => {
    const off = buildOffsets(rows);
    for (let y = 0; y < off[off.length - 1]!; y += 7) {
      const r = findStartRow(off, y);
      expect(off[r]! <= y).toBe(true);
      expect(off[r + 1]! > y).toBe(true);
    }
  });

  it('空列表不崩', () => {
    const off = buildOffsets([]);
    expect(computeVisible(off, 0, 400, 5)).toEqual({ start: 0, end: 0, padTop: 0 });
  });
});
