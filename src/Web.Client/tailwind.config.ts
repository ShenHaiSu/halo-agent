// src/Web.Client/tailwind.config.ts
import type { Config } from 'tailwindcss';

/**
 * Tailwind v3.4.16 配置。
 *
 * ============ 三条关键决定 ============
 *  ① 所有颜色都来自 CSS 变量（hsl(var(--x)) 形式）
 *     → 换肤只需改 :root 上的变量，Tailwind 的 utility 类自动跟随
 *  ② 不配置 darkMode: 'media'
 *     → Win7 没有 prefers-color-scheme；主题完全由 <html data-theme> 决定
 *  ③ content 只扫 src/，且显式排除 __gallery 的示例代码
 *     → 避免把示例里的大类名打进产物
 * ============ ============
 */
export default {
  content: ['./index.html', './src/**/*.{vue,ts}'],
  darkMode: ['selector', '[data-theme="dark"]'],
  theme: {
    extend: {
      colors: {
        // 语义色（全部走 CSS 变量）
        bg: {
          DEFAULT: 'hsl(var(--bg) / <alpha-value>)',
          subtle: 'hsl(var(--bg-subtle) / <alpha-value>)',
          muted: 'hsl(var(--bg-muted) / <alpha-value>)',
          elevated: 'hsl(var(--bg-elevated) / <alpha-value>)',
          overlay: 'hsl(var(--bg-overlay) / <alpha-value>)',
        },
        fg: {
          DEFAULT: 'hsl(var(--fg) / <alpha-value>)',
          muted: 'hsl(var(--fg-muted) / <alpha-value>)',
          subtle: 'hsl(var(--fg-subtle) / <alpha-value>)',
          inverse: 'hsl(var(--fg-inverse) / <alpha-value>)',
        },
        border: {
          DEFAULT: 'hsl(var(--border) / <alpha-value>)',
          strong: 'hsl(var(--border-strong) / <alpha-value>)',
          subtle: 'hsl(var(--border-subtle) / <alpha-value>)',
        },
        accent: {
          DEFAULT: 'hsl(var(--accent) / <alpha-value>)',
          hover: 'hsl(var(--accent-hover) / <alpha-value>)',
          fg: 'hsl(var(--accent-fg) / <alpha-value>)',
          subtle: 'hsl(var(--accent-subtle) / <alpha-value>)',
        },
        success: {
          DEFAULT: 'hsl(var(--success) / <alpha-value>)',
          subtle: 'hsl(var(--success-subtle) / <alpha-value>)',
        },
        warning: {
          DEFAULT: 'hsl(var(--warning) / <alpha-value>)',
          subtle: 'hsl(var(--warning-subtle) / <alpha-value>)',
        },
        danger: {
          DEFAULT: 'hsl(var(--danger) / <alpha-value>)',
          subtle: 'hsl(var(--danger-subtle) / <alpha-value>)',
        },
        info: {
          DEFAULT: 'hsl(var(--info) / <alpha-value>)',
          subtle: 'hsl(var(--info-subtle) / <alpha-value>)',
        },
      },

      fontFamily: {
        sans: ['var(--font-sans)'],
        mono: ['var(--font-mono)'],
      },

      fontSize: {
        // 与 §8.1 的字号表一致
        '2xs': ['var(--text-2xs)', { lineHeight: 'var(--leading-2xs)' }],
        xs: ['var(--text-xs)', { lineHeight: 'var(--leading-xs)' }],
        sm: ['var(--text-sm)', { lineHeight: 'var(--leading-sm)' }],
        base: ['var(--text-base)', { lineHeight: 'var(--leading-base)' }],
        lg: ['var(--text-lg)', { lineHeight: 'var(--leading-lg)' }],
        xl: ['var(--text-xl)', { lineHeight: 'var(--leading-xl)' }],
        '2xl': ['var(--text-2xl)', { lineHeight: 'var(--leading-2xl)' }],
        '3xl': ['var(--text-3xl)', { lineHeight: 'var(--leading-3xl)' }],
      },

      borderRadius: {
        sm: 'var(--radius-sm)',
        DEFAULT: 'var(--radius-base)',
        md: 'var(--radius-md)',
        lg: 'var(--radius-lg)',
        xl: 'var(--radius-xl)',
        '2xl': 'var(--radius-2xl)',
        full: '9999px',
      },

      boxShadow: {
        sm: 'var(--shadow-sm)',
        DEFAULT: 'var(--shadow-base)',
        md: 'var(--shadow-md)',
        lg: 'var(--shadow-lg)',
        xl: 'var(--shadow-xl)',
        focus: 'var(--shadow-focus)',
      },

      spacing: {
        // 4px 基准之外，补充几个语义间距
        gutter: 'var(--space-gutter)',
        'sidebar-w': 'var(--layout-sidebar-width)',
        'titlebar-h': 'var(--layout-titlebar-height)',
      },

      transitionDuration: {
        fast: 'var(--duration-fast)',
        base: 'var(--duration-base)',
        slow: 'var(--duration-slow)',
      },

      transitionTimingFunction: {
        'out-quart': 'cubic-bezier(0.25, 1, 0.5, 1)',
        'out-expo': 'cubic-bezier(0.16, 1, 0.3, 1)',
      },

      zIndex: {
        base: '0',
        dropdown: '1000',
        sticky: '1020',
        overlay: '1030',
        modal: '1040',
        popover: '1050',
        toast: '1060',
        tooltip: '1070',
      },

      keyframes: {
        'fade-in': {
          from: { opacity: '0' },
          to: { opacity: '1' },
        },
        'slide-up': {
          from: { opacity: '0', transform: 'translateY(4px)' },
          to: { opacity: '1', transform: 'translateY(0)' },
        },
        'scale-in': {
          from: { opacity: '0', transform: 'scale(0.96)' },
          to: { opacity: '1', transform: 'scale(1)' },
        },
      },
      animation: {
        'fade-in': 'fade-in var(--duration-base) var(--ease-out)',
        'slide-up': 'slide-up var(--duration-base) var(--ease-out)',
        'scale-in': 'scale-in var(--duration-fast) var(--ease-out)',
      },
    },
  },
  plugins: [],
} satisfies Config;
