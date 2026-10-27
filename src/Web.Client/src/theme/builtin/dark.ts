// src/Web.Client/src/theme/builtin/dark.ts
import type { ThemeFile } from '../types';

/**
 * 内置深色主题。
 *
 * ============ 深色配色原则 ============
 *  ① 背景不要纯黑（`0 0% 10%` 是最深的可接受值；纯黑会让边框完全不可见）
 *  ② 前景不要纯白（`92%` 比 `100%` 更柔和，长时间阅读不刺眼）
 *  ③ 语义色的**亮度**要抬高（深色底上用 38% 的绿几乎看不见，要 46%）
 *  ④ subtle 变体在深色下是"比背景稍亮"而不是"稍暗"
 *  ⑤ 阴影要更重（深色底上的阴影容易被吞掉）
 * ============ ============
 */
export const dark: ThemeFile = {
  id: 'dark',
  name: '深色',
  author: 'builtin',
  version: 1,
  basedOn: 'dark',
  tokens: {
    bg: '222 16% 11%',
    'bg-subtle': '222 15% 14%',
    'bg-muted': '222 14% 18%',
    'bg-elevated': '222 15% 15%',
    'bg-overlay': '0 0% 0% / 0.5',

    fg: '220 14% 92%',
    'fg-muted': '220 9% 64%',
    'fg-subtle': '220 8% 48%',
    'fg-inverse': '222 16% 11%',

    border: '220 12% 24%',
    'border-strong': '220 11% 34%',
    'border-subtle': '220 13% 19%',

    accent: '210 96% 60%',
    'accent-hover': '210 96% 66%',
    'accent-fg': '222 30% 8%',
    'accent-subtle': '212 55% 22%',

    success: '152 58% 46%',
    'success-subtle': '152 42% 19%',
    warning: '35 92% 58%',
    'warning-subtle': '35 60% 20%',
    danger: '0 78% 62%',
    'danger-subtle': '0 52% 20%',
    info: '198 82% 58%',
    'info-subtle': '198 55% 19%',

    'shadow-sm': '0 1px 2px hsl(0 0% 0% / 0.34)',
    'shadow-base': '0 1px 3px hsl(0 0% 0% / 0.44), 0 1px 2px hsl(0 0% 0% / 0.26)',
    'shadow-md': '0 4px 10px hsl(0 0% 0% / 0.48), 0 2px 4px hsl(0 0% 0% / 0.28)',
    'shadow-lg': '0 12px 28px hsl(0 0% 0% / 0.56), 0 4px 8px hsl(0 0% 0% / 0.32)',
    'shadow-xl': '0 24px 52px hsl(0 0% 0% / 0.64), 0 8px 16px hsl(0 0% 0% / 0.38)',
    'shadow-focus': '0 0 0 3px hsl(210 96% 60% / 0.42)',
  },
};
