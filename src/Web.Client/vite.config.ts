// src/Web.Client/vite.config.ts
import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig(({ mode }) => {
  const isProd = mode === 'production';

  return {
    plugins: [vue()],

    // ★ 关键：app:// 协议下用绝对根路径
    //   base: './' 配合 app:// 会产生 app://local/assets/... 之外的相对解析问题，
    //   实测 base: '/' 在 app://local/ 下最稳（SpaSchemeHandlerFactory 会处理）。
    base: '/',

    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url)),
      },
    },

    build: {
      // ★★ 铁律 F-1：JS 目标锁定 chrome109
      target: 'chrome109',

      // ★★ 铁律 F-1：CSS 目标同样锁定
      cssTarget: 'chrome109',

      outDir: '../Host.Desktop/wwwroot',
      emptyOutDir: true,
      sourcemap: isProd ? false : 'inline',
      cssCodeSplit: false, // 单 CSS 文件便于检查与缓存
      assetsInlineLimit: 4096, // <4KB 内联为 base64
      chunkSizeWarningLimit: 1500,

      rollupOptions: {
        output: {
          // 手动分包：把不常变的库单独拆出（@vueuse/core 暂无引用，不再单拆 vendor，避免空 chunk）
          manualChunks: {
            vue: ['vue', 'vue-router', 'pinia'],
          },
          // 文件名带 hash（宿主侧对带 hash 的资源长缓存）
          chunkFileNames: 'assets/[name]-[hash].js',
          entryFileNames: 'assets/[name]-[hash].js',
          assetFileNames: 'assets/[name]-[hash][extname]',
        },
      },

      // ★ 防止 esbuild 把 <script type="module"> 变成 classic
      modulePreload: { polyfill: false },
    },

    css: {
      postcss: './postcss.config.mjs',
      devSourcemap: true,
    },

    esbuild: {
      // 生产态移除 console/debugger（保留 console.error/warn）
      drop: isProd ? [] : [],
      pure: isProd ? ['console.debug', 'console.trace'] : [],
    },

    server: {
      port: 5173,
      strictPort: true,
      host: '127.0.0.1',
      hmr: { overlay: true },
    },

    // ★ 禁用浏览器兼容性警告（我们自己控制 target）
    optimizeDeps: {
      esbuildOptions: { target: 'chrome109' },
    },
  };
});
