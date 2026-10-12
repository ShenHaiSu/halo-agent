import { createApp } from 'vue';
import { createPinia } from 'pinia';
import naive from 'naive-ui';
import './styles/main.css';
import App from './App.vue';
import { initHostBridge } from './bridge/hostBridge';

/**
 * 显式控制 CSS 注入顺序：
 * 在 Tailwind CSS 样式表加载完成后、根组件挂载前，向 document.head 末尾插入 <meta name="naive-ui-style" /> 锚点标签，
 * 确保 Naive UI 的 CSS-in-JS 样式始终位于 Tailwind Base Preflight 之后，彻底杜绝 <n-button type="primary"> 背景透明冲突。
 */
function ensureNaiveUiStyleMetaAnchor(): void {
  const existing = document.head.querySelector('meta[name="naive-ui-style"]');
  if (existing) {
    existing.remove();
  }
  const meta = document.createElement('meta');
  meta.name = 'naive-ui-style';
  document.head.appendChild(meta);
}

async function bootstrap(): Promise<void> {
  ensureNaiveUiStyleMetaAnchor();
  await initHostBridge();

  const app = createApp(App);
  const pinia = createPinia();

  app.use(pinia);
  app.use(naive);
  app.mount('#app');
}

bootstrap();
