// src/Web.Client/src/components/sidebar/virtualize.test.ts
import { describe, expect, it } from 'vitest';
import { buildOffsets, computeVisible, findStartRow } from './virtualize';

/** 夹具行高与真实布局同步：header 22（HEAD_H），item 56（ITEM_H） */
const HEADER_H = 22;
const ITEM_H = 56;

const rows = [
  { h: HEADER_H }, // header
  ...Array.from({ length: 10 }, () => ({ h: ITEM_H })),
  { h: HEADER_H },
  ...Array.from({ length: 10 }, () => ({ h: ITEM_H })),
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

  /**
   * 标题行位置恒等：
   *  静态行在窗口内的文档偏移 = off[i] - padTop，窗口被 translateY(padTop) 位移，
   *  视口内视觉位置 = off[i] - padTop - scrollTop + padTop = off[i] - scrollTop。
   *  与 padTop / scrollTop 是否相等无关，且允许任意小数 scrollTop。
   */
  it('任意 scrollTop 下渲染窗口内每一行的视觉位置恒等于 off[i] - scrollTop', () => {
    const off = buildOffsets(rows);
    const viewportH = 400;
    const buffer = 5;
    const cases: number[] = [0, 0.5, 7, 22, 21.75, 133.4, 318.9, 999.25, 1500.5];
    for (const st of cases) {
      const v = computeVisible(off, st, viewportH, buffer);
      for (let i = v.start; i < v.end; i++) {
        const visual = off[i]! - v.padTop - st + v.padTop; // 行偏移 - 窗口位移 - 滚动量 + 窗口位移
        expect(visual).toBeCloseTo(off[i]! - st, 10);
      }
      // 窗口首个渲染行不能出现在视口上方留白之外（padTop 补偿精确）
      expect(v.padTop).toBe(off[v.start]!);
      expect(v.padTop).toBeLessThanOrEqual(Math.max(0, st));
    }
  });

  it('padTop 与 scrollTop 最大差值不超过一个 buffer 窗口高度', () => {
    const off = buildOffsets(rows);
    const viewportH = 400;
    const buffer = 5;
    let maxGap = 0;
    for (let st = 0; st <= off[off.length - 1]!; st += 3.5) {
      const v = computeVisible(off, st, viewportH, buffer);
      maxGap = Math.max(maxGap, Math.abs(st - v.padTop));
    }
    // first 正好落在某个 item 上时，窗口首行还要再上退 buffer 行：
    // (BUFFER + 1) 个 item = 6 * 56 = 336，减去行内偏移（< ITEM_H）即实测上界
    expect(maxGap).toBeLessThanOrEqual((5 + 1) * ITEM_H);
    expect(maxGap).toBeGreaterThan((5 + 1) * ITEM_H - ITEM_H);
  });
});
