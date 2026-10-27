// src/Web.Client/src/theme/builtin/index.ts
import type { ThemeFile } from '../types';
import { light } from './light';
import { dark } from './dark';
import { midnight } from './midnight';

/** 内置主题表（id → 主题） */
export const builtinThemes: Record<string, ThemeFile> = {
  light,
  dark,
  midnight,
};

/** 供 bench.ts 使用 */
export const pendingThemeSnapshot = builtinThemes;

/** 主题在设置页里的展示顺序 */
export const builtinThemeOrder = ['light', 'dark', 'midnight'] as const;

export { light, dark, midnight };
