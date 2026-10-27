import type { Config } from 'tailwindcss'

/**
 * Tailwind 配置。
 *
 * 设计原则：**所有颜色/圆角/时长/字体都指向 CSS 变量**，
 * 这样运行时换主题只改 :root 上的变量，Tailwind 生成的类名不变，
 * 实现"零重排换肤"。
 *
 * 不允许在这里写死十六进制颜色（除非是纯黑/纯白这种永远不变的）。
 */
export default {
  content: ['./index.html', './src/**/*.{vue,ts}'],

  // 不用 darkMode: 'media' —— Win7 没有系统级暗黑 API，主题完全应用内控制
  darkMode: 'class',

  theme: {
    extend: {
      colors: {
        // 用 rgb(var(--x) / <alpha-value>) 才能让 bg-canvas/50 这类透明度修饰符生效
        canvas: 'rgb(var(--bg-canvas) / <alpha-value>)',
        surface: 'rgb(var(--bg-surface) / <alpha-value>)',
        elevated: 'rgb(var(--bg-elevated) / <alpha-value>)',
        overlay: 'rgb(var(--bg-overlay) / <alpha-value>)',

        fg: 'rgb(var(--fg-primary) / <alpha-value>)',
        muted: 'rgb(var(--fg-muted) / <alpha-value>)',
        faint: 'rgb(var(--fg-faint) / <alpha-value>)',

        line: 'rgb(var(--border-subtle) / <alpha-value>)',
        'line-strong': 'rgb(var(--border-strong) / <alpha-value>)',

        accent: 'rgb(var(--accent) / <alpha-value>)',
        'accent-fg': 'rgb(var(--accent-fg) / <alpha-value>)',
        'accent-weak': 'rgb(var(--accent-weak) / <alpha-value>)',

        ok: 'rgb(var(--ok) / <alpha-value>)',
        warn: 'rgb(var(--warn) / <alpha-value>)',
        danger: 'rgb(var(--danger) / <alpha-value>)',
        info: 'rgb(var(--info) / <alpha-value>)',
      },

      borderRadius: {
        sm: 'var(--radius-sm)',
        DEFAULT: 'var(--radius-md)',
        lg: 'var(--radius-lg)',
        xl: 'var(--radius-xl)',
      },

      fontFamily: {
        // Win7 没有 Microsoft YaHei UI，回退到旧版 YaHei
        ui: ['var(--font-ui)'],
        mono: ['var(--font-mono)'],
      },

      transitionDuration: {
        fast: 'var(--dur-fast)',
        DEFAULT: 'var(--dur-base)',
        slow: 'var(--dur-slow)',
      },

      transitionTimingFunction: {
        standard: 'var(--ease-standard)',
        emphasized: 'var(--ease-emphasized)',
      },

      spacing: {
        // 微调：命令卡片的内缩进
        1.75: '0.4375rem',
      },

      keyframes: {
        pulseSoft: {
          '0%,100%': { opacity: '1' },
          '50%': { opacity: '0.45' },
        },
        shimmer: {
          '0%': { transform: 'translateX(-100%)' },
          '100%': { transform: 'translateX(100%)' },
        },
      },

      animation: {
        'pulse-soft': 'pulseSoft 1.6s var(--ease-standard) infinite',
        shimmer: 'shimmer 1.8s var(--ease-standard) infinite',
      },
    },
  },

  plugins: [],
} satisfies Config
