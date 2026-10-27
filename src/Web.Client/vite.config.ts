import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'
import tailwindcss from 'tailwindcss'
import tailwindNesting from 'tailwindcss/nesting'
import postcssImport from 'postcss-import'
import presetEnv from 'postcss-preset-env'
import autoprefixer from 'autoprefixer'
import cssnano from 'cssnano'

/**
 * HaloAgent 前端构建配置。
 *
 * ============ 铁律 ============
 * 1. target 与 cssTarget 都必须是 'chrome109'
 *    （Chromium 109 = 支持 Win7 的最后版本）
 * 2. 不开启任何会引入 WebGPU / modern CSS 的插件
 * 3. 产物输出到宿主工程的 wwwroot，由 app://local/ 加载
 * ==============================
 */
export default defineConfig({
  plugins: [vue()],

  // app://local/index.html 加载时，资源必须用相对路径
  // （绝对路径 /assets/x.js 在 app:// 下会被解析成 app://local/assets/x.js，
  //  而 FolderSchemeHandlerFactory 恰好能处理，但相对路径更稳）
  base: './',

  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },

  build: {
    // ---- 铁律：JS 语法降级目标 ----
    target: 'chrome109',

    // ---- 铁律：CSS 语法降级目标 ----
    cssTarget: 'chrome109',

    outDir: '../Host.Desktop/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    assetsInlineLimit: 4096,

    cssCodeSplit: true,
    cssMinify: 'esbuild',

    // 产物大小告警阈值（CEF 本地加载，1MB 也不算大，但超过就该看看）
    chunkSizeWarningLimit: 1024,

    rollupOptions: {
      output: {
        // 把体积大、变动少的依赖拆出来，便于缓存与排查
        // 注意：markdown 块（markdown-it/dompurify/highlight.js）在 P4 真正引入
        // 这些库时再加回来；P0 的自检页用不到，空 chunk 会产生构建警告。
        manualChunks: {
          vendor: ['vue', 'pinia'],
        },
      },
    },
  },

  server: {
    port: 5173,
    strictPort: true,
    // 宿主用 http://localhost:5173 加载，必须允许该来源
    host: '127.0.0.1',
  },

  css: {
    // 注意：PostCSS 管道内联于此（数组形式），而不是引用 ./postcss.config.ts。
    // 原因有二：
    //   1. Vite 5 的 postcss-load-config 加载 .ts 文件需要 ts-node；
    //   2. 内联对象写法（plugins: {...}）不被 Vite 接受，必须是插件数组。
    // postcss.config.ts 仍保留为 canonical 文本 + 锁校验目标，改动时两边同步。
    postcss: {
      plugins: [
        postcssImport(),

        // Tailwind v3 的嵌套插件（输出时展平，不产出原生 CSS nesting）
        tailwindNesting(),

        tailwindcss(),

        /**
         * 关键：把 CSS 精确降级到 chrome 109。
         * - stage: 2 让 preset-env 启用处于 Stage 2 的特性
         * - 显式关掉 Chrome 109 不支持的函数，避免它们被"polyfill 成 var 链"后仍然报错
         *   （有些特性 preset-env 无法真正降级，只能禁用，让我们在源码里就别写）
         */
        presetEnv({
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
            'gap-properties': false, // 原生支持
            'logical-properties-and-values': false,
          },
        }),

        autoprefixer({
          overrideBrowserslist: 'chrome 109',
        }),

        // 生产环境才压缩（开发态保留可读性）
        ...(process.env.NODE_ENV === 'production' ? [cssnano({ preset: 'default' })] : []),
      ],
    },
  },

  // 减少 esbuild 在 Win7 目标上的探测
  esbuild: {
    target: 'chrome109',
  },
})
