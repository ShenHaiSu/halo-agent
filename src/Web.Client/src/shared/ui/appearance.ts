import type { GlobalThemeOverrides } from 'naive-ui';
import { haloDarkThemeOverrides, haloLightThemeOverrides } from './naiveTheme';

export type ThemeMode = 'dark' | 'light';
export type ChatWidthMode = 'centered' | 'full';

/** 前端外观唯一真相源（持久化于 localStorage['halo.appearance.v1']） */
export interface AppearanceSettings {
  themeMode: ThemeMode;
  /** 主题色：6 预设 + 自定义 hex */
  themeColor: string;
  /** 全局基准字号 px，12~16 */
  fontSize: number;
  chineseFont: string;
  englishFont: string;
  /** 等宽字体（高级折叠项） */
  monoFont: string;
  /** 会话主界面宽度：'centered' 限宽居中 | 'full' 占满 */
  chatWidthMode: ChatWidthMode;
}

export const DEFAULT_APPEARANCE: AppearanceSettings = {
  themeMode: 'dark',
  themeColor: '#3b82f6',
  fontSize: 13,
  chineseFont: 'Microsoft YaHei UI',
  englishFont: 'Segoe UI',
  monoFont: 'Consolas',
  chatWidthMode: 'centered',
};

export const THEME_COLOR_PRESETS: Array<{ label: string; value: string }> = [
  { label: '科技蓝', value: '#3b82f6' },
  { label: '深邃蓝', value: '#2563eb' },
  { label: '青绿', value: '#10b981' },
  { label: '紫', value: '#8b5cf6' },
  { label: '橙', value: '#f59e0b' },
  { label: '玫红', value: '#f43f5e' },
];

export const CHINESE_FONT_OPTIONS = [
  'Microsoft YaHei UI',
  'Microsoft YaHei',
  'PingFang SC',
  'SimSun',
  'SimHei',
];

export const ENGLISH_FONT_OPTIONS = [
  'Segoe UI',
  'Inter',
  'Arial',
  'Helvetica Neue',
  'system-ui',
];

export const FONT_SIZE_OPTIONS = [12, 13, 14, 15, 16];

export const APPEARANCE_STORAGE_KEY = 'halo.appearance.v1';

/** 6 预设 × 暗亮的 hover/pressed/suppl 三档映射（Chrome109 安全，不引入颜色库） */
interface PrimaryShades {
  hover: string;
  pressed: string;
  suppl: string;
}

const PRIMARY_SHADES: Record<string, Record<ThemeMode, PrimaryShades>> = {
  '#3b82f6': {
    dark: { hover: '#60a5fa', pressed: '#2563eb', suppl: '#1d4ed8' },
    light: { hover: '#60a5fa', pressed: '#1d4ed8', suppl: '#1e40af' },
  },
  '#2563eb': {
    dark: { hover: '#3b82f6', pressed: '#1d4ed8', suppl: '#1e40af' },
    light: { hover: '#3b82f6', pressed: '#1d4ed8', suppl: '#1e40af' },
  },
  '#10b981': {
    dark: { hover: '#34d399', pressed: '#059669', suppl: '#047857' },
    light: { hover: '#34d399', pressed: '#047857', suppl: '#065f46' },
  },
  '#8b5cf6': {
    dark: { hover: '#a78bfa', pressed: '#7c3aed', suppl: '#6d28d9' },
    light: { hover: '#a78bfa', pressed: '#6d28d9', suppl: '#5b21b6' },
  },
  '#f59e0b': {
    dark: { hover: '#fbbf24', pressed: '#d97706', suppl: '#b45309' },
    light: { hover: '#fbbf24', pressed: '#b45309', suppl: '#92400e' },
  },
  '#f43f5e': {
    dark: { hover: '#fb7185', pressed: '#e11d48', suppl: '#be123c' },
    light: { hover: '#fb7185', pressed: '#be123c', suppl: '#9f1239' },
  },
};

export function isValidHexColor(value: unknown): value is string {
  return typeof value === 'string' && /^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{3})$/.test(value.trim());
}

/** 归一化为 6 位小写 hex；非法返回 null */
export function normalizeHexColor(value: unknown): string | null {
  if (!isValidHexColor(value)) return null;
  let hex = (value as string).trim().toLowerCase();
  if (hex.length === 4) {
    hex = `#${hex[1]}${hex[1]}${hex[2]}${hex[2]}${hex[3]}${hex[3]}`;
  }
  return hex;
}

function quoteFont(name: string): string {
  const clean = name.trim().replace(/^["']+|["']+$/g, '');
  return `"${clean}"`;
}

/** 中英文字体合成栈：body 跟随 var(--font-sans) 自动生效 */
export function buildFontStack(appearance: Pick<AppearanceSettings, 'chineseFont' | 'englishFont'>): string {
  const zh = appearance.chineseFont?.trim() ? quoteFont(appearance.chineseFont) : '"Microsoft YaHei UI"';
  const en = appearance.englishFont?.trim() ? quoteFont(appearance.englishFont) : '"Segoe UI"';
  return `${zh}, ${en}, "PingFang SC", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`;
}

export function buildMonoFontStack(monoFont: string): string {
  const first = monoFont?.trim() ? quoteFont(monoFont) : '"Consolas"';
  return `${first}, "JetBrains Mono", "Cascadia Mono", Consolas, "Courier New", monospace`;
}

function resolvePrimaryShades(themeColor: string, mode: ThemeMode): PrimaryShades {
  const key = themeColor.trim().toLowerCase();
  const hit = PRIMARY_SHADES[key];
  if (hit) return hit[mode];
  // 自定义色回退原色（Naive 允许 hover/pressed 与主色同值）
  return { hover: themeColor, pressed: themeColor, suppl: themeColor };
}

/**
 * 以现有两套 naiveTheme 为基座，仅覆盖字体/字号/主色系。
 * mode 同时接受 'dark' | 'light' 与 boolean（true = dark），保持调用方兼容。
 */
export function buildHaloThemeOverrides(
  appearance: AppearanceSettings,
  mode: ThemeMode | boolean
): GlobalThemeOverrides {
  const themeMode: ThemeMode = typeof mode === 'boolean' ? (mode ? 'dark' : 'light') : mode;
  const base = themeMode === 'dark' ? haloDarkThemeOverrides : haloLightThemeOverrides;
  const shades = resolvePrimaryShades(appearance.themeColor, themeMode);
  const fontSize = Math.min(16, Math.max(12, Math.round(appearance.fontSize)));
  const fontSizeSmall = Math.max(11, fontSize - 1);
  const fontFamily = buildFontStack(appearance);
  const fontFamilyMono = buildMonoFontStack(appearance.monoFont);

  return {
    ...base,
    common: {
      ...base.common,
      fontFamily,
      fontFamilyMono,
      fontSize: `${fontSize}px`,
      fontSizeMedium: `${fontSize}px`,
      fontSizeSmall: `${fontSizeSmall}px`,
      primaryColor: appearance.themeColor,
      primaryColorHover: shades.hover,
      primaryColorPressed: shades.pressed,
      primaryColorSuppl: shades.suppl,
    },
    Tabs: {
      ...base.Tabs,
      tabFontSizeMedium: `${fontSize}px`,
      tabFontSizeSmall: `${fontSizeSmall}px`,
    },
  };
}
