import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import path from 'path';

export default defineConfig({
  base: './',
  plugins: [vue()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    // 铁律：锁定编译目标为 chrome109，自动降级高版本 JS/CSS
    target: 'chrome109',
    cssTarget: 'chrome109',
    outDir: '../Host.Desktop/wwwroot',
    // need22-P5 J5：不再清空整个 wwwroot（宿主另放文件会被误删）；
    // 仅清 assets/ 由 prebuild（scripts/clean-assets.mjs）负责。
    emptyOutDir: false,
    sourcemap: false,
    assetsInlineLimit: 4096,
    chunkSizeWarningLimit: 500,
    rollupOptions: {
      output: {
        // need22-P5 J5：naive 全量引入拆包（echarts / naive-ui / vue 全家桶独立 chunk，主包减半）。
        manualChunks: {
          'vue-vendor': ['vue', 'vue-router', 'pinia'],
          'naive-ui': ['naive-ui'],
          echarts: ['echarts'],
        },
      },
    },
  },
});
