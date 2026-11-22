/**
 * 外观 Store 测试。
 *
 * 两组关注点：
 * 1) 主题色 CSS 变量同步：语义色走 `rgb(var(--xxx-rgb) / <alpha-value>)` 后，accent 系列
 *    读 `--accent-primary-rgb`；自定义主题色必须 hex 与 rgb 三元组同步写，否则打回默认蓝。
 * 2) 后端真相源（config/appearance.json）：dev(http://localhost:5173) 与 release(app://local)
 *    的 localStorage 按 origin 隔离，故后端文件为真相源，localStorage 仅作首帧同步快照。
 *    覆盖：后端值覆盖、无文件迁移、桥缺失兜底、debounce 落盘、flush/reset。
 */
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import { APPEARANCE_STORAGE_KEY, DEFAULT_APPEARANCE, hexToRgbTriplet } from '@shared/ui/appearance';
import { useAppearanceStore } from './appearanceStore';

const { bridgeMock } = vi.hoisted(() => ({
  bridgeMock: {
    getAppearance: vi.fn(),
    setAppearance: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', () => ({
  getHostBridge: () => bridgeMock,
}));

describe('hexToRgbTriplet', () => {
  it('把 6 位 hex 拆成空格分隔三元组', () => {
    expect(hexToRgbTriplet('#3b82f6')).toBe('59 130 246');
    expect(hexToRgbTriplet('#F43F5E')).toBe('244 63 94');
  });

  it('兼容 3 位简写（复用 normalizeHexColor）', () => {
    expect(hexToRgbTriplet('#abc')).toBe('170 187 204');
  });

  it('非法输入返回 null', () => {
    expect(hexToRgbTriplet('rgb(1,2,3)')).toBeNull();
    expect(hexToRgbTriplet('var(--accent-primary)')).toBeNull();
    expect(hexToRgbTriplet(undefined)).toBeNull();
  });
});

describe('appearanceStore.applyToDom 主题色同步', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    bridgeMock.getAppearance.mockReset().mockResolvedValue({ ok: false, code: 'APPEARANCE_NOT_FOUND' });
    bridgeMock.setAppearance.mockReset().mockResolvedValue({ ok: true, data: true });
    const root = document.documentElement;
    root.removeAttribute('style');
    root.classList.remove('dark', 'light');
    vi.useRealTimers();
  });

  it('默认主题色同时写 hex 与 rgb 三元组', async () => {
    const store = useAppearanceStore();
    await store.loadAndApply();
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--accent-primary')).toBe('#3b82f6');
    expect(root.style.getPropertyValue('--accent-primary-rgb')).toBe('59 130 246');
  });

  it('自定义主题色后三元组跟着变（否则 accent/<alpha> 会停在默认蓝）', async () => {
    const store = useAppearanceStore();
    await store.loadAndApply();
    store.update({ themeColor: '#f43f5e' });
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--accent-primary')).toBe('#f43f5e');
    expect(root.style.getPropertyValue('--accent-primary-rgb')).toBe('244 63 94');
  });

  it('reset 回默认色时三元组同步归位', async () => {
    const store = useAppearanceStore();
    await store.loadAndApply();
    store.update({ themeColor: '#f43f5e' });
    store.reset();
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--accent-primary')).toBe('#3b82f6');
    expect(root.style.getPropertyValue('--accent-primary-rgb')).toBe('59 130 246');
  });

  it('暗/亮切换落到 html class 上（主题变量由 class 驱动）', async () => {
    const store = useAppearanceStore();
    await store.loadAndApply();
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    store.setThemeMode('light');
    expect(document.documentElement.classList.contains('light')).toBe(true);
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });
});

describe('appearanceStore 后端真相源（dev/release 配置互通）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    bridgeMock.getAppearance.mockReset().mockResolvedValue({ ok: false, code: 'APPEARANCE_NOT_FOUND' });
    bridgeMock.setAppearance.mockReset().mockResolvedValue({ ok: true, data: true });
    const root = document.documentElement;
    root.removeAttribute('style');
    root.classList.remove('dark', 'light');
    vi.useRealTimers();
  });

  it('后端有值时覆盖 localStorage 快照并刷新快照（release 能读到 dev 的设置）', async () => {
    localStorage.setItem(APPEARANCE_STORAGE_KEY, JSON.stringify({ ...DEFAULT_APPEARANCE, themeColor: '#f43f5e' }));
    bridgeMock.getAppearance.mockResolvedValue({
      ok: true,
      data: { ...DEFAULT_APPEARANCE, themeColor: '#10b981', chatWidthMode: 'full' },
    });

    const store = useAppearanceStore();
    await store.loadAndApply();

    expect(store.themeColor).toBe('#10b981');
    expect(store.chatWidthMode).toBe('full');
    expect(document.documentElement.style.getPropertyValue('--accent-primary')).toBe('#10b981');
    expect(JSON.parse(localStorage.getItem(APPEARANCE_STORAGE_KEY)!).themeColor).toBe('#10b981');
  });

  it('后端无文件（APPEARANCE_NOT_FOUND）时把 localStorage 快照迁移到后端', async () => {
    localStorage.setItem(APPEARANCE_STORAGE_KEY, JSON.stringify({ ...DEFAULT_APPEARANCE, themeColor: '#f43f5e', chatWidthMode: 'full' }));
    bridgeMock.getAppearance.mockResolvedValue({ ok: false, code: 'APPEARANCE_NOT_FOUND' });

    const store = useAppearanceStore();
    await store.loadAndApply();

    expect(bridgeMock.setAppearance).toHaveBeenCalledTimes(1);
    const payload = JSON.parse(bridgeMock.setAppearance.mock.calls[0][0]);
    expect(payload.themeColor).toBe('#f43f5e');
    expect(payload.chatWidthMode).toBe('full');
  });

  it('桥缺失/抛错时保留 localStorage 快照且不阻断启动', async () => {
    localStorage.setItem(APPEARANCE_STORAGE_KEY, JSON.stringify({ ...DEFAULT_APPEARANCE, themeColor: '#8b5cf6' }));
    bridgeMock.getAppearance.mockRejectedValue(new Error('BRIDGE_NOT_CONNECTED'));
    bridgeMock.setAppearance.mockRejectedValue(new Error('BRIDGE_NOT_CONNECTED'));

    const store = useAppearanceStore();
    await expect(store.loadAndApply()).resolves.toBeDefined();
    expect(store.themeColor).toBe('#8b5cf6');
    expect(document.documentElement.style.getPropertyValue('--accent-primary')).toBe('#8b5cf6');
  });

  it('update 300ms 后写后端，连续更新只写一次（debounce）', async () => {
    vi.useFakeTimers();
    bridgeMock.getAppearance.mockResolvedValue({ ok: true, data: DEFAULT_APPEARANCE });
    const store = useAppearanceStore();
    await store.loadAndApply();
    bridgeMock.setAppearance.mockClear();

    store.update({ fontSize: 14 });
    store.update({ fontSize: 15 });
    store.update({ fontSize: 16 });
    expect(bridgeMock.setAppearance).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(300);
    expect(bridgeMock.setAppearance).toHaveBeenCalledTimes(1);
    expect(JSON.parse(bridgeMock.setAppearance.mock.calls[0][0]).fontSize).toBe(16);
    vi.useRealTimers();
  });

  it('flush 立即写后端并取消 pending debounce（页面隐藏时不重复写）', async () => {
    vi.useFakeTimers();
    bridgeMock.getAppearance.mockResolvedValue({ ok: true, data: DEFAULT_APPEARANCE });
    const store = useAppearanceStore();
    await store.loadAndApply();
    bridgeMock.setAppearance.mockClear();

    store.update({ themeColor: '#2563eb' });
    await store.flush();
    expect(bridgeMock.setAppearance).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(400);
    expect(bridgeMock.setAppearance).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });

  it('reset 也触发后端落盘', async () => {
    vi.useFakeTimers();
    bridgeMock.getAppearance.mockResolvedValue({
      ok: true,
      data: { ...DEFAULT_APPEARANCE, themeColor: '#f43f5e' },
    });
    const store = useAppearanceStore();
    await store.loadAndApply();
    bridgeMock.setAppearance.mockClear();

    store.reset();
    await vi.advanceTimersByTimeAsync(300);
    expect(bridgeMock.setAppearance).toHaveBeenCalledTimes(1);
    expect(JSON.parse(bridgeMock.setAppearance.mock.calls[0][0]).themeColor).toBe('#3b82f6');
    vi.useRealTimers();
  });
});
