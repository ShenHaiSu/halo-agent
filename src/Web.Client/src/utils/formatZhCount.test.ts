// src/Web.Client/src/utils/formatZhCount.test.ts
// 用量单位口径单测：<1万原始值 / 万档两位 / 舍入溢出进位 / 亿兆档 / 负数非有限。
import { describe, expect, it } from 'vitest';
import { formatZhCount } from './format';

describe('formatZhCount ', () => {
  it('<1万原始值（含千分位）', () => {
    expect(formatZhCount(0)).toBe('0');
    expect(formatZhCount(999)).toBe('999');
    expect(formatZhCount(1000)).toBe('1,000'); // 千档删除
    expect(formatZhCount(9999)).toBe('9,999'); // 旧 1万
  });
  it('万档两位', () => {
    expect(formatZhCount(10000)).toBe('1.00万');
    expect(formatZhCount(12340)).toBe('1.23万');
    expect(formatZhCount(12340000)).toBe('1234.00万'); // 旧 1.2亿
    expect(formatZhCount(9999500)).toBe('999.95万'); // 旧 0.1亿（核心行为变更）
  });
  it('舍入溢出进位', () => {
    expect(formatZhCount(99999999)).toBe('1.00亿');
  });
  it('亿/兆档两位', () => {
    expect(formatZhCount(1e8)).toBe('1.00亿');
    expect(formatZhCount(1.123e8)).toBe('1.12亿');
    expect(formatZhCount(1.5e8)).toBe('1.50亿');
    expect(formatZhCount(1e12)).toBe('1.00兆');
    expect(formatZhCount(2.5e12)).toBe('2.50兆');
  });
  it('负数与非有限保持', () => {
    expect(formatZhCount(-5)).toBe('-5');
    expect(formatZhCount(NaN)).toBe('—');
    expect(formatZhCount(Infinity)).toBe('—');
  });
});
