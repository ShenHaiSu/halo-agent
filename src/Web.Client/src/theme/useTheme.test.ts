// src/Web.Client/src/theme/useTheme.test.ts
import { describe, expect, it, beforeEach, vi, afterEach } from 'vitest';

/**
 * 系统跟随 / 手动覆盖的行为。
 *
 * ============ 三态语义 ============
 *  mode = 'light'  → 固定浅色（即使系统是深色）
 *  mode = 'dark'   → 固定深色
 *  mode = 'system' → 跟随系统；系统变化时**自动**切换
 *  手动选择任一具体主题后，mode 不再是 'system'（覆盖生效）
 * ============ Win7 的特殊性 ============
 *  Win7 的 Chrome 109 对 prefers-color-scheme 恒返回 false，
 *  所以 mode='system' 在 Win7 上等价于 'light'。这是可接受的降级。
 * ============
 */
const STORAGE_KEY = 'haloagent.theme';

describe('useTheme 模式', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.resetModules();
  });
  afterEach(() => vi.restoreAllMocks());

  function mockSystemDark(dark: boolean) {
    window.matchMedia = ((q: string) => ({
      matches: dark && q.includes('dark'),
      media: q,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })) as unknown as typeof window.matchMedia;
  }

  it('默认是 light', async () => {
    const { useTheme, initTheme } = await import('./useTheme');
    mockSystemDark(false);
    initTheme();
    expect(useTheme().mode.value).toBe('light');
  });

  it('读 localStorage', async () => {
    localStorage.setItem(STORAGE_KEY, 'dark');
    const { useTheme, initTheme } = await import('./useTheme');
    mockSystemDark(false);
    initTheme();
    expect(useTheme().mode.value).toBe('dark');
  });

  it('system + 系统深色 → 解析为 dark', async () => {
    localStorage.setItem(STORAGE_KEY, 'system');
    const { useTheme, initTheme, resolveThemeId } = await import('./useTheme');
    void useTheme;
    mockSystemDark(true);
    initTheme();
    expect(resolveThemeId('system', true)).toBe('dark');
    expect(resolveThemeId('system', false)).toBe('light');
  });

  it('手动选择覆盖 system', async () => {
    localStorage.setItem(STORAGE_KEY, 'system');
    const { useTheme, initTheme } = await import('./useTheme');
    mockSystemDark(true);
    initTheme();
    const t = useTheme();
    t.setMode('light');
    await new Promise((r) => setTimeout(r, 0));
    expect(t.mode.value).toBe('light');
    expect(localStorage.getItem(STORAGE_KEY)).toBe('light');
  });

  it('写入 localStorage 失败时不抛错', async () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('QuotaExceeded');
    });
    const { useTheme, initTheme } = await import('./useTheme');
    mockSystemDark(false);
    expect(() => {
      initTheme();
      useTheme().setMode('dark');
    }).not.toThrow();
  });
});
