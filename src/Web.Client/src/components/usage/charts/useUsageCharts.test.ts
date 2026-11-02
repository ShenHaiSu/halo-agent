// src/Web.Client/src/components/usage/charts/useUsageCharts.test.ts
// token 左轴五等分刻度：max=interval×5、interval 整数、max 覆盖数据最大值。
import { describe, expect, it } from 'vitest';
import { niceTokenScale } from './useUsageCharts';

describe('niceTokenScale', () => {
  it('空数据回落 5/1', () => {
    expect(niceTokenScale(0)).toEqual({ max: 5, interval: 1 });
    expect(niceTokenScale(NaN)).toEqual({ max: 5, interval: 1 });
  });

  it('整数小量：100 → max 100 interval 20', () => {
    expect(niceTokenScale(100)).toEqual({ max: 100, interval: 20 });
  });

  it('非整步长上取整：max=interval×5 且覆盖 dataMax', () => {
    for (const v of [1, 7, 99, 123, 999, 1234, 9999, 45678, 123456, 2500000]) {
      const { max, interval } = niceTokenScale(v);
      expect(Number.isInteger(interval)).toBe(true);
      expect(interval).toBeGreaterThanOrEqual(1);
      expect(max).toBe(interval * 5);
      expect(max).toBeGreaterThanOrEqual(v);
    }
  });

  it('边界升档不爆区间：9999 仍是 5 段整数', () => {
    const { max, interval } = niceTokenScale(9999);
    expect(max).toBe(interval * 5);
    expect(max).toBeGreaterThanOrEqual(9999);
  });
});
