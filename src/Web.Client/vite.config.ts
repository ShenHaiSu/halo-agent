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
    emptyOutDir: true,
    sourcemap: false,
    assetsInlineLimit: 4096,
    chunkSizeWarningLimit: 2500, // 全量引入 Naive UI 属本地桌面端正常设计，放宽体积告警阈值
  },
});
