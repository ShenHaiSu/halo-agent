// src/Web.Client/src/theme/themes.test.ts
import { describe, expect, it } from 'vitest';
import { builtinThemes, builtinThemeOrder, light, dark, midnight } from './builtin';
import { THEME_TOKEN_KEYS, type ThemeFile } from './types';
import { validateThemeTokens } from './validate';

/**
 * 三个内置主题的数据完整性。
 *
 * ============ 这组测试守住什么 ============
 *  ① 少一个 token → 该 token 回退到 CSS 默认值（light 的值）→ 深色主题下出现白块
 *  ② 值格式错 → hsl(#hex / 1) 非法 → 整条声明失效 → 白底白字
 *  ③ basedOn 写错 → 判断"是否深色"出错 → 标题栏颜色与内容不匹配
 *  这三类问题在 UI 上都表现为"某个地方颜色不对"，极难定位。
 *  用测试在 0.5 秒内锁死。
 * ============
 */
describe('内置主题', () => {
  const cases: Array<[string, ThemeFile]> = [
    ['light', light],
    ['dark', dark],
    ['midnight', midnight],
  ];

  it('按 builtinThemeOrder 定义了 3 个主题', () => {
    expect(builtinThemeOrder).toEqual(['light', 'dark', 'midnight']);
    expect(Object.keys(builtinThemes).sort()).toEqual(['dark', 'light', 'midnight']);
  });

  it.each(cases)('%s 的 token key 集合与 THEME_TOKEN_KEYS 完全一致', (_name, theme) => {
    const keys = Object.keys(theme.tokens).sort();
    const want = [...THEME_TOKEN_KEYS].sort();
    expect(keys).toEqual(want);
  });

  it.each(cases)('%s 的每个 token 值都通过 validateThemeTokens', (_name, theme) => {
    const result = validateThemeTokens(theme.tokens);
    expect(result.errors).toEqual([]);
    expect(result.ok).toBe(true);
    expect(Object.keys(result.tokens)).toHaveLength(THEME_TOKEN_KEYS.length);
  });

  it.each(cases)('%s 的元数据合法', (name, theme) => {
    expect(theme.id).toBe(name);
    expect(theme.name.length).toBeGreaterThan(0);
    expect(theme.version).toBeGreaterThanOrEqual(1);
    expect(['light', 'dark']).toContain(theme.basedOn);
  });

  it('basedOn 与视觉直觉一致', () => {
    expect(light.basedOn).toBe('light');
    expect(dark.basedOn).toBe('dark');
    expect(midnight.basedOn).toBe('dark');
  });

  it('浅色与深色的 bg 亮度差异足够大（> 40%）', () => {
    const lum = (v: string) => Number(/(\d+(?:\.\d+)?)%\s*$/.exec(v.trim())?.[1] ?? '0');
    expect(lum(light.tokens['bg']!)).toBeGreaterThan(90);
    expect(lum(dark.tokens['bg']!)).toBeLessThan(30);
    expect(lum(midnight.tokens['bg']!)).toBeLessThan(20);
  });

  it('前景与背景的明度差 >= 60%（可读性底线）', () => {
    const lum = (v: string) => Number(/(\d+(?:\.\d+)?)%\s*$/.exec(v.trim())?.[1] ?? '0');
    for (const [, t] of cases) {
      const d = Math.abs(lum(t.tokens['bg']!) - lum(t.tokens['fg']!));
      expect(d, `${t.id} 的 bg/fg 明度差过小`).toBeGreaterThanOrEqual(60);
    }
  });

  it('所有主题都定义了 accent 的 hover 与 fg', () => {
    for (const [, t] of cases) {
      expect(t.tokens['accent']).toBeTruthy();
      expect(t.tokens['accent-hover']).toBeTruthy();
      expect(t.tokens['accent-fg']).toBeTruthy();
    }
  });
});
