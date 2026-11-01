// src/Web.Client/src/utils/formatZhCount.test.ts
// P17-03 中文数量单位单测：档位 / 溢出进位 / 浮点 / 负数 / 非有限。
import { describe, expect, it } from 'vitest';
import { formatZhCount } from './format';

describe('formatZhCount', () => {
  it('1000 以下精确值', () => {
    expect(formatZhCount(0)).toBe('0');
    expect(formatZhCount(999)).toBe('999');
  });
  it('千档', () => {
    expect(formatZhCount(1000)).toBe('1千');
    expect(formatZhCount(1500)).toBe('1.5千');
  });
  it('溢出进位 9999→1万', () => {
    expect(formatZhCount(9999)).toBe('1万');
  });
  it('万档', () => {
    expect(formatZhCount(10000)).toBe('1万');
    expect(formatZhCount(123456)).toBe('12.3万');
  });
  it('亿 / 兆档', () => {
    expect(formatZhCount(1e8)).toBe('1亿');
    expect(formatZhCount(1.5e8)).toBe('1.5亿');
    expect(formatZhCount(1e12)).toBe('1兆');
    expect(formatZhCount(2.5e12)).toBe('2.5兆');
  });
  it('万→亿溢出 9999500→0.1亿', () => {
    expect(formatZhCount(9999500)).toBe('0.1亿');
  });
  it('浮点 tick 先 round', () => {
    expect(formatZhCount(2500.5)).toBe('2.5千');
  });
  it('负数与非有限', () => {
    expect(formatZhCount(-5)).toBe('-5');
    expect(formatZhCount(NaN)).toBe('—');
    expect(formatZhCount(Infinity)).toBe('—');
  });
});
