import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { buildHaloThemeOverrides } from '@shared/ui/appearance';
import { useAppearanceStore } from '@/stores/appearanceStore';

const { bridgeMock } = vi.hoisted(() => ({
  bridgeMock: {
    getAppearance: vi.fn(),
    setAppearance: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', () => ({ getHostBridge: () => bridgeMock }));

const moduleDir = path.resolve(process.cwd(), 'src/features/settings/components/agent');

describe('智能体行为页主题色跟随', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    bridgeMock.setAppearance.mockReset().mockResolvedValue({ ok: true, data: true });
    const root = document.documentElement;
    root.removeAttribute('style');
    root.classList.remove('dark', 'light');
  });

  it.each([
    ['#10b981', '16 185 129'],
    ['#8b5cf6', '139 92 246'],
    ['#f43f5e', '244 63 94'],
  ])('切换颜色 %s 时 CSS 三元组与 naive-ui 主色同步更新', (hex, rgb) => {
    const store = useAppearanceStore();
    store.update({ themeColor: hex });
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--accent-primary')).toBe(hex);
    expect(root.style.getPropertyValue('--accent-primary-rgb')).toBe(rgb);
    const overrides = buildHaloThemeOverrides(store.settings, store.themeMode);
    expect(overrides.common?.primaryColor).toBe(hex);
  });

  it('自定义 hex 走同一条链路，不使用预设色时仍生效', () => {
    const store = useAppearanceStore();
    store.update({ themeColor: '#2dd4bf' });
    expect(document.documentElement.style.getPropertyValue('--accent-primary-rgb')).toBe('45 212 191');
    expect(buildHaloThemeOverrides(store.settings, store.themeMode).common?.primaryColor).toBe('#2dd4bf');
  });

  it('切换暗亮模式不会用模式默认色覆盖用户选定主题色', () => {
    const store = useAppearanceStore();
    store.update({ themeColor: '#8b5cf6' });
    store.setThemeMode('light');
    expect(document.documentElement.style.getPropertyValue('--accent-primary')).toBe('#8b5cf6');
    expect(buildHaloThemeOverrides(store.settings, 'light').common?.primaryColor).toBe('#8b5cf6');
    store.setThemeMode('dark');
    expect(buildHaloThemeOverrides(store.settings, 'dark').common?.primaryColor).toBe('#8b5cf6');
  });

  it('成功/警告/错误色属于状态语义，不跟随用户主题色', () => {
    const store = useAppearanceStore();
    const before = buildHaloThemeOverrides(store.settings, 'dark').common;
    store.update({ themeColor: '#10b981' });
    const after = buildHaloThemeOverrides(store.settings, 'dark').common;
    expect(after?.successColor).toBe(before?.successColor);
    expect(after?.warningColor).toBe(before?.warningColor);
    expect(after?.errorColor).toBe(before?.errorColor);
  });
});

describe('智能体行为页主色源码扫描', () => {
  const allowedFallback = /var\(\s*--accent-primary(?:-rgb)?[^,)]*,\s*#(?:3b82f6|2563eb)\s*\)/gi;

  function collectModuleSources(): Array<{ file: string; text: string }> {
    const sources: Array<{ file: string; text: string }> = [];
    const walk = (dir: string): void => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
          walk(full);
        } else if (/\.(?:vue|css)$/.test(entry.name)) {
          sources.push({ file: path.relative(moduleDir, full), text: readFileSync(full, 'utf8') });
        }
      }
    };
    walk(moduleDir);
    return sources;
  }

  it('模块 CSS / SFC 中不允许把默认蓝写死为主色（仅允许作为 var 兜底）', () => {
    const sources = collectModuleSources();
    expect(sources.length).toBeGreaterThan(0);
    for (const source of sources) {
      const withoutFallbacks = source.text.replace(allowedFallback, '');
      expect(/#(?:3b82f6|2563eb)\b/i.test(withoutFallbacks), `${source.file} 写死了默认蓝`).toBe(false);
    }
  });

  it('模块 CSS 使用语义变量声明主题色', () => {
    const css = sourcesFind('agent-behavior.css');
    expect(css).toContain('var(--accent-primary');
    expect(css).toContain('rgb(var(--accent-primary-rgb');
    expect(css).not.toContain('color-mix(');
  });

  function sourcesFind(name: string): string {
    return readFileSync(path.join(moduleDir, name), 'utf8');
  }
});
