// src/Web.Client/src/theme/themeFallback.test.ts
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * ============ 三类非法主题的降级 ============
 *  ① 主题文件不存在 → 回退 light
 *  ② tokens 全是 #hex → 全部丢弃 → 回退 light（因为 tokens 为空）
 *  ③ 一半合法一半非法 → 应用合法项 + 记录错误（可用性优先）
 *
 *  ★ 第三类是"部分降级"：宁可显示一半对的颜色，也不要整屏白底白字。
 *    但必须把错误暴露在设置页（lastError），让用户知道主题有问题。
 * ============
 */
describe('非法主题降级', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.resetModules();
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.removeAttribute('style');
  });

  it('主题不存在时回退 light，且不抛错', async () => {
    const { initTheme, useTheme } = await import('./useTheme');
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

    localStorage.setItem('haloagent.theme', 'no-such-theme');
    initTheme();
    await new Promise((r) => setTimeout(r, 10));

    const t = useTheme();
    expect(t.themeId.value).toBe('light');

    // 界面颜色必须可用
    const bg = document.documentElement.style.getPropertyValue('--bg');
    expect(bg.trim().length).toBeGreaterThan(0);

    warnSpy.mockRestore();
  });

  it('非法的内置主题 id 不会导致白底白字', async () => {
    const { initTheme } = await import('./useTheme');
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    localStorage.setItem('haloagent.theme', 'dark-hacker');
    initTheme();
    await new Promise((r) => setTimeout(r, 10));

    const bg = document.documentElement.style.getPropertyValue('--bg');
    const fg = document.documentElement.style.getPropertyValue('--fg');
    // 两者都不能为空，且不能是同一个值
    expect(bg.trim()).not.toBe('');
    expect(fg.trim()).not.toBe('');
    expect(bg.trim()).not.toBe(fg.trim());
  });

  it('lastError 会记录非法 token 的原因', async () => {
    const { validateThemeTokens } = await import('./validate');
    const r = validateThemeTokens({ bg: '#fff', fg: 'rgb(0,0,0)' });
    expect(r.ok).toBe(false);
    expect(r.errors.length).toBe(2);
    expect(r.errors[0]).toContain('bg');
  });
});
