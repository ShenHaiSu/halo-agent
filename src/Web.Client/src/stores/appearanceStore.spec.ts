/**
 * 外观 Store 的主题色 CSS 变量测试。
 *
 * 背景：语义色走 `rgb(var(--xxx-rgb) / <alpha-value>)` 后，accent 系列（如 border-accent/40）
 * 读的是 `--accent-primary-rgb`；而用户可在「外观设置」自定义主题色，运行时写的是
 * `--accent-primary`（hex）。两者必须同步，否则自定义主题色后所有 accent/<alpha> 类会
 * 打回默认蓝 —— 比修复前更糟。本用例守的就是这条同步。
 */
import { describe, expect, it, beforeEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import { hexToRgbTriplet } from '@shared/ui/appearance';
import { useAppearanceStore } from './appearanceStore';

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
    const root = document.documentElement;
    root.removeAttribute('style');
    root.classList.remove('dark', 'light');
  });

  it('默认主题色同时写 hex 与 rgb 三元组', () => {
    const store = useAppearanceStore();
    store.loadAndApply();
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--accent-primary')).toBe('#3b82f6');
    expect(root.style.getPropertyValue('--accent-primary-rgb')).toBe('59 130 246');
  });

  it('自定义主题色后三元组跟着变（否则 accent/<alpha> 会停在默认蓝）', () => {
    const store = useAppearanceStore();
    store.loadAndApply();
    store.update({ themeColor: '#f43f5e' });
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--accent-primary')).toBe('#f43f5e');
    expect(root.style.getPropertyValue('--accent-primary-rgb')).toBe('244 63 94');
  });

  it('reset 回默认色时三元组同步归位', () => {
    const store = useAppearanceStore();
    store.loadAndApply();
    store.update({ themeColor: '#f43f5e' });
    store.reset();
    const root = document.documentElement;
    expect(root.style.getPropertyValue('--accent-primary')).toBe('#3b82f6');
    expect(root.style.getPropertyValue('--accent-primary-rgb')).toBe('59 130 246');
  });

  it('暗/亮切换落到 html class 上（主题变量由 class 驱动）', () => {
    const store = useAppearanceStore();
    store.loadAndApply();
    expect(document.documentElement.classList.contains('dark')).toBe(true);
    store.setThemeMode('light');
    expect(document.documentElement.classList.contains('light')).toBe(true);
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });
});
