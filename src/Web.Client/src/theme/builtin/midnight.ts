// src/Web.Client/src/theme/builtin/midnight.ts
import type { ThemeFile } from '../types';

/**
 * 内置「午夜」主题：深色 + 紫调强调色。
 *
 * ============ 定位 ============
 *  给"想要深色但觉得纯灰太冷"的用户一个选择。
 *  强调色偏紫（265°），背景带轻微蓝紫（225°），
 *  与 dark 主题的差别只在色相，亮度关系完全一致。
 *  这样它能在所有同一位置替换 dark 而不破坏对比度。
 * ============ ============
 */
export const midnight: ThemeFile = {
  id: 'midnight',
  name: '午夜',
  author: 'builtin',
  version: 1,
  basedOn: 'dark',
  tokens: {
    bg: '228 18% 10%',
    'bg-subtle': '228 17% 13%',
    'bg-muted': '228 16% 17%',
    'bg-elevated': '228 17% 14%',
    'bg-overlay': '0 0% 0% / 0.5',

    fg: '226 16% 91%',
    'fg-muted': '226 10% 63%',
    'fg-subtle': '226 9% 47%',
    'fg-inverse': '228 18% 10%',

    border: '228 14% 23%',
    'border-strong': '228 13% 33%',
    'border-subtle': '228 15% 18%',

    accent: '265 88% 68%',
    'accent-hover': '265 88% 74%',
    'accent-fg': '265 40% 10%',
    'accent-subtle': '265 45% 22%',

    success: '155 58% 47%',
    'success-subtle': '155 40% 19%',
    warning: '38 92% 60%',
    'warning-subtle': '38 58% 21%',
    danger: '0 80% 64%',
    'danger-subtle': '0 52% 21%',
    info: '200 82% 60%',
    'info-subtle': '200 55% 20%',

    'shadow-sm': '0 1px 2px hsl(265 30% 4% / 0.4)',
    'shadow-base': '0 1px 3px hsl(265 30% 4% / 0.48), 0 1px 2px hsl(265 30% 4% / 0.28)',
    'shadow-md': '0 4px 10px hsl(265 30% 4% / 0.52), 0 2px 4px hsl(265 30% 4% / 0.3)',
    'shadow-lg': '0 12px 28px hsl(265 30% 4% / 0.6), 0 4px 8px hsl(265 30% 4% / 0.34)',
    'shadow-xl': '0 24px 52px hsl(265 30% 4% / 0.68), 0 8px 16px hsl(265 30% 4% / 0.4)',
    'shadow-focus': '0 0 0 3px hsl(265 88% 68% / 0.45)',
  },
};
