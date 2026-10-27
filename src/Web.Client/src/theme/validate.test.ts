// src/Web.Client/src/theme/validate.test.ts
import { describe, expect, it } from 'vitest';
import { validateThemeTokens } from './validate';

/**
 * validateThemeTokens 的边界。
 *
 * ============ 五类必须拒绝的输入 ============
 *  ① #hex 形式的颜色（会被拼成非法的 hsl(#1a1a22 / 1)）
 *  ② rgb()/rgba()
 *  ③ oklch()/color-mix() 等 Chrome 110+ 函数
 *  ④ 非字符串值
 *  ⑤ 未知 token / 空值
 * ============ 三类必须接受的输入 ============
 *  ① "H S% L%"
 *  ② "H S% L% / 0.5"（带透明度，用于 --bg-overlay）
 *  ③ 带多余空格的同义写法（要归一化）
 * ============
 */
describe('validateThemeTokens', () => {
  const base = { bg: '0 0% 100%' };

  it('接受合法的 H S% L%', () => {
    const r = validateThemeTokens(base);
    expect(r.ok).toBe(true);
    expect(r.tokens.bg).toBe('0 0% 100%');
  });

  it('接受带透明度的值', () => {
    const r = validateThemeTokens({ 'bg-overlay': '220 20% 10% / 0.42' });
    expect(r.ok).toBe(true);
    expect(r.tokens['bg-overlay']).toBe('220 20% 10% / 0.42');
  });

  it('接受带多余空格的值（归一化后判定）', () => {
    const r = validateThemeTokens({ bg: '  0   0%   100%  ' });
    expect(r.ok).toBe(true);
  });

  it('拒绝 #hex 并给出可读错误', () => {
    const r = validateThemeTokens({ bg: '#1a1a22' });
    expect(r.ok).toBe(false);
    expect(r.tokens.bg).toBeUndefined();
    expect(r.errors.join()).toContain('格式必须是');
    expect(r.errors.join()).toContain('#hex');
  });

  it('拒绝 rgb() / rgba()', () => {
    expect(validateThemeTokens({ fg: 'rgb(20 20 30)' }).ok).toBe(false);
    expect(validateThemeTokens({ fg: 'rgba(20,20,30,0.5)' }).ok).toBe(false);
  });

  it('拒绝 oklch() / color-mix()', () => {
    const r1 = validateThemeTokens({ accent: 'oklch(0.7 0.1 200)' });
    expect(r1.ok).toBe(false);
    expect(r1.errors.join()).toContain('Chrome 109');
    expect(validateThemeTokens({ accent: 'color-mix(in srgb, red, blue)' }).ok).toBe(false);
  });

  it('拒绝非字符串与非对象', () => {
    expect(validateThemeTokens(null).ok).toBe(false);
    expect(validateThemeTokens([]).ok).toBe(false);
    expect(validateThemeTokens('abc').ok).toBe(false);
    expect(validateThemeTokens({ bg: 123 }).ok).toBe(false);
  });

  it('拒绝空字符串', () => {
    const r = validateThemeTokens({ bg: '   ' });
    expect(r.ok).toBe(false);
    expect(r.tokens.bg).toBeUndefined();
  });

  it('未知 token 被忽略但仍记录错误', () => {
    const r = validateThemeTokens({ ...base, 'not-a-token': '1 1% 1%' });
    expect(r.ok).toBe(false);
    expect(r.errors.join()).toContain('未知 token');
    expect(r.tokens['not-a-token' as 'bg']).toBeUndefined();
    expect(r.tokens.bg).toBe('0 0% 100%'); // 合法项保留
  });

  it('部分非法时合法项仍被返回（可用性设计）', () => {
    const r = validateThemeTokens({ bg: '0 0% 100%', fg: '#000' });
    expect(r.ok).toBe(false);
    expect(r.tokens.bg).toBe('0 0% 100%');
    expect(r.tokens.fg).toBeUndefined();
  });
});
