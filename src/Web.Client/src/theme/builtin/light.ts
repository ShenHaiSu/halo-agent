// src/Web.Client/src/theme/builtin/light.ts
import type { ThemeFile } from '../types';

/**
 * 内置浅色主题。
 *
 * ============ 配色原则 ============
 *  ① 背景三档：bg（主）/ bg-subtle（略暗，用于侧栏）/ bg-muted（更暗，用于代码块）
 *  ② 前景三档：fg（正文）/ fg-muted（次要）/ fg-subtle（极次要，如分隔符文字）
 *  ③ 边框三档：border-subtle（几乎看不见）/ border（默认）/ border-strong（强调）
 *  ④ 强调色：中性蓝（210°），在 Win7 的 ClearType 下最稳
 *  ⑤ 语义色饱和度不过高（避免在低色域屏幕上刺眼）
 * ============ ============
 */
export const light: ThemeFile = {
  id: 'light',
  name: '浅色',
  author: 'builtin',
  version: 1,
  basedOn: 'light',
  tokens: {
    // ---- 背景 ----
    bg: '0 0% 100%',
    'bg-subtle': '220 14% 98%',
    'bg-muted': '220 14% 96%',
    'bg-elevated': '0 0% 100%',
    'bg-overlay': '220 20% 10% / 0.42',

    // ---- 前景 ----
    fg: '222 15% 16%',
    'fg-muted': '220 9% 46%',
    'fg-subtle': '220 8% 62%',
    'fg-inverse': '0 0% 100%',

    // ---- 边框 ----
    border: '220 13% 90%',
    'border-strong': '220 12% 78%',
    'border-subtle': '220 14% 95%',

    // ---- 强调色 ----
    accent: '212 92% 48%',
    'accent-hover': '212 92% 43%',
    'accent-fg': '0 0% 100%',
    'accent-subtle': '212 92% 96%',

    // ---- 语义色 ----
    success: '152 62% 38%',
    'success-subtle': '152 60% 95%',
    warning: '35 92% 48%',
    'warning-subtle': '35 92% 95%',
    danger: '0 72% 50%',
    'danger-subtle': '0 72% 96%',
    info: '198 85% 44%',
    'info-subtle': '198 85% 95%',

    // ---- 阴影（浅色用很淡的黑色）
    'shadow-sm': '0 1px 2px hsl(220 20% 20% / 0.06)',
    'shadow-base': '0 1px 3px hsl(220 20% 20% / 0.1), 0 1px 2px hsl(220 20% 20% / 0.05)',
    'shadow-md': '0 4px 10px hsl(220 20% 20% / 0.1), 0 2px 4px hsl(220 20% 20% / 0.05)',
    'shadow-lg': '0 12px 28px hsl(220 20% 20% / 0.12), 0 4px 8px hsl(220 20% 20% / 0.06)',
    'shadow-xl': '0 24px 52px hsl(220 20% 20% / 0.16), 0 8px 16px hsl(220 20% 20% / 0.08)',
    'shadow-focus': '0 0 0 3px hsl(212 92% 48% / 0.3)',
  },
};
