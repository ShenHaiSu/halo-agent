export default {
  plugins: {
    'postcss-import': {},

    // Tailwind v3 的嵌套插件（输出时展平，不产出原生 CSS nesting）
    'tailwindcss/nesting': {},

    tailwindcss: {},

    /**
     * 关键：把 CSS 精确降级到 chrome 109。
     * - stage: 2 让 preset-env 启用处于 Stage 2 的特性
     * - 显式关掉 Chrome 109 不支持的函数，避免它们被"polyfill 成 var 链"后仍然报错
     *   （有些特性 preset-env 无法真正降级，只能禁用，让我们在源码里就别写）
     */
    'postcss-preset-env': {
      stage: 2,
      browsers: 'chrome 109',
      features: {
        'nesting-rules': false, // 交给 tailwind 的 nesting 插件处理
        'custom-properties': false, // CSS 变量原生支持，必须保留
        'color-mix': false, // Chrome 111+ 才有，禁止
        'oklab-function': false, // Chrome 111+，禁止
        'lab-function': false, // 同上
        'light-dark-function': false, // Chrome 123+，禁止
        'has-pseudo-class': false, // Chrome 105+ 原生支持，无需降级
        'gap-properties': false, // 原生支持（Chrome 105+ 自带容器查询，无需 preset-env 处理）
        'logical-properties-and-values': false,
      },
    },

    autoprefixer: {
      overrideBrowserslist: 'chrome 109',
    },

    // 生产环境才压缩（开发态保留可读性）
    ...(process.env.NODE_ENV === 'production' ? { cssnano: { preset: 'default' } } : {}),
  },
}
