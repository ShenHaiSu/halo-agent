/** @type {import('tailwindcss').Config} */
export default {
  darkMode: 'class',
  content: ['./index.html', './src/**/*.{vue,js,ts,jsx,tsx}'],
  theme: {
    extend: {
      fontFamily: {
        sans: [
          '"Microsoft YaHei UI"',
          '"Microsoft YaHei"',
          '"PingFang SC"',
          '-apple-system',
          'BlinkMacSystemFont',
          '"Segoe UI"',
          'sans-serif',
        ],
        mono: ['"JetBrains Mono"', '"Cascadia Mono"', 'Consolas', '"Courier New"', 'monospace'],
      },
      colors: {
        // 语义色必须写成 rgb(var(--xxx-rgb) / <alpha-value>)，不能用纯 var(--hex)：
        // 纯 var() 过不了 Tailwind 的 parseColor，border-accent/40 这类透明度修饰会被
        // 静默丢弃（规则为空 → 被裁剪 → 边框回落到 preflight 的 #e5e7eb）。
        // 三元组色值由 main.css 的 --xxx-rgb 按暗/亮主题切换，勿改回纯 var(--xxx)。
        app: 'rgb(var(--bg-app-rgb) / <alpha-value>)',
        sidebar: 'rgb(var(--bg-sidebar-rgb) / <alpha-value>)',
        surface: 'rgb(var(--bg-surface-rgb) / <alpha-value>)',
        elevated: 'rgb(var(--bg-elevated-rgb) / <alpha-value>)',
        subtle: 'rgb(var(--border-subtle-rgb) / <alpha-value>)',
        primary: 'rgb(var(--text-primary-rgb) / <alpha-value>)',
        secondary: 'rgb(var(--text-secondary-rgb) / <alpha-value>)',
        accent: 'rgb(var(--accent-primary-rgb) / <alpha-value>)',
      },
    },
  },
  plugins: [],
};
