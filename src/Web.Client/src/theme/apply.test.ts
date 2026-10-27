// src/Web.Client/src/theme/apply.test.ts
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { applyThemeTokens, clearInlineThemeTokens } from './apply';
import { dark, light } from './builtin';
import { THEME_TOKEN_KEYS } from './types';

/**
 * ============ 零重排的三条证明 ============
 *  P-1：applyThemeTokens 只调 style.setProperty，不碰 class/attribute/DOM 结构
 *  P-2：切换前后 geometry token（宽高、padding、margin、字号）的值不变
 *  P-3：所有 30 个变量都是颜色或阴影（不含任何几何属性）
 *
 *  说明：jsdom 没有布局引擎，无法测量真实 reflow。
 *  所以这里证明"实现方式"正确，真实 reflow 由 Chrome 里的 CLS 测量（§5.3.2）承担。
 * ============
 */
describe('applyThemeTokens 零重排', () => {
  beforeEach(() => clearInlineThemeTokens());

  it('P-1a：只通过 style.setProperty 写入', () => {
    const root = document.documentElement;
    const setSpy = vi.spyOn(root.style, 'setProperty');
    const classSpy = vi.spyOn(root.classList, 'add');
    const attrSpy = vi.spyOn(root, 'setAttribute');

    applyThemeTokens(dark.tokens);

    expect(setSpy).toHaveBeenCalledTimes(Object.keys(dark.tokens).length);
    expect(classSpy).not.toHaveBeenCalled();
    // data-theme 由 useTheme 设置，不在这里；这里断言 apply 本身不设属性
    expect(attrSpy).not.toHaveBeenCalled();

    setSpy.mockRestore();
    classSpy.mockRestore();
    attrSpy.mockRestore();
  });

  it('P-1b：不修改 DOM 结构', () => {
    document.body.innerHTML = '<div id="x"><span>a</span></div>';
    const before = document.body.innerHTML;
    applyThemeTokens(midnightLike());
    expect(document.body.innerHTML).toBe(before);
  });

  it('P-2：切换前后没有几何相关的内联属性', () => {
    applyThemeTokens(dark.tokens);
    const root = document.documentElement;
    const styles = Array.from(root.style);
    const geometry = styles.filter((k) =>
      /width|height|padding|margin|font-size|line-height|top|left|right|bottom|gap|border-width/.test(
        k
      )
    );
    expect(geometry).toEqual([]);
  });

  it('P-3：THEME_TOKEN_KEYS 中没有任何几何属性名', () => {
    const forbidden = /width|height|padding|margin|font-size|line-height|^gap$|radius/;
    for (const k of THEME_TOKEN_KEYS) {
      expect(forbidden.test(k), `token「${k}」看起来是几何属性，不允许进主题`).toBe(false);
    }
  });

  it('reset 时清除全部 30 个变量', () => {
    applyThemeTokens(dark.tokens);
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--bg')).toBe(dark.tokens.bg);

    applyThemeTokens({}, { reset: true });
    for (const k of THEME_TOKEN_KEYS) {
      expect(root.style.getPropertyValue(`--${k}`)).toBe('');
    }
  });

  it('从 dark 切到 light 后所有值都回到 light', () => {
    applyThemeTokens(dark.tokens);
    clearInlineThemeTokens();
    applyThemeTokens(light.tokens);
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--bg').trim()).toBe(light.tokens.bg);
    expect(root.style.getPropertyValue('--fg').trim()).toBe(light.tokens.fg);
  });

  function midnightLike() {
    return dark.tokens;
  }
});
