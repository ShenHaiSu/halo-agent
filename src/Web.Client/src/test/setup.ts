// src/Web.Client/src/test/setup.ts
import { afterEach, beforeEach, vi } from 'vitest';

/**
 * 测试环境初始化。
 *
 * ============ 为什么需要 mock matchMedia ============
 *  jsdom 没有 matchMedia，而 useTheme 会调它。
 *  给一个默认实现（matches: false），需要模拟深色时单独改。
 * ============ ============
 */
beforeEach(() => {
  if (!window.matchMedia) {
    window.matchMedia = ((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })) as unknown as typeof window.matchMedia;
  }

  // 每个测试前清空 localStorage（避免主题泄漏）
  try {
    localStorage.clear();
  } catch {
    /* 忽略 */
  }
});

afterEach(() => {
  // 清掉所有内联样式变量（避免测试互相影响）
  const root = document.documentElement;
  for (const key of Array.from(root.style)) {
    if (key.startsWith('--')) root.style.removeProperty(key);
  }
});
