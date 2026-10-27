// src/Web.Client/src/theme/types.ts
export interface ThemeFile {
  id: string;
  name: string;
  author?: string;
  version: number;
  /** 基于哪个内置主题（用于"重置单项"） */
  basedOn?: 'light' | 'dark' | string;
  tokens: Record<string, string>;
}

/** 主题 token 的白名单：只有这些 key 会被应用 */
export const THEME_TOKEN_KEYS = [
  'bg',
  'bg-subtle',
  'bg-muted',
  'bg-elevated',
  'bg-overlay',
  'fg',
  'fg-muted',
  'fg-subtle',
  'fg-inverse',
  'border',
  'border-strong',
  'border-subtle',
  'accent',
  'accent-hover',
  'accent-fg',
  'accent-subtle',
  'success',
  'success-subtle',
  'warning',
  'warning-subtle',
  'danger',
  'danger-subtle',
  'info',
  'info-subtle',
  'shadow-sm',
  'shadow-base',
  'shadow-md',
  'shadow-lg',
  'shadow-xl',
  'shadow-focus',
] as const;

export type ThemeTokenKey = (typeof THEME_TOKEN_KEYS)[number];
