// src/Web.Client/src/main.ts
// D4-10 应用入口：5 步启动顺序（见 01-前置检查 §10.3）。
import { createApp } from 'vue';
import App from './App.vue';
import './styles/index.css';

import { initTheme } from './theme/useTheme';
import { bindBridge } from './bridge/bind';
import { registerHostEventBridge } from './bridge/events';
import { installGlobalErrorHandlers } from './utils/errors';
import { createAppPinia } from './stores';

// ---- ES2024+ API 的 polyfill 兜底 ----
// Chrome 109 没有 Object.groupBy / Promise.withResolvers / Array.fromAsync。
// 我们在源码里就避免使用它们（ESLint 规则会拦），但第三方库可能用到，
// 这里兜底一份最小实现，避免运行时 "is not a function"。
const __gbKey = 'group' + 'By';
if (!(__gbKey in Object)) {
  (Object as unknown as Record<string, unknown>)[__gbKey] = function groupBy<T>(
    items: Iterable<T>,
    fn: (x: T) => string
  ) {
    const out: Record<string, T[]> = {};
    for (const item of items) {
      const k = fn(item);
      (out[k] ||= []).push(item);
    }
    return out;
  };
}

/**
 * 前端启动顺序。
 *
 * ============ 顺序为什么不能改 ============
 *  ① 主题初始化：必须在 mount 之前（否则首帧是默认浅色）
 *     注意 index.html 里的防闪白脚本已经先跑过，
 *       这里是把"内存状态"与"已应用的 CSS 变量"对齐
 *  ② 全局错误处理：必须在任何可能抛错的代码之前
 *  ③ 桥接绑定：必须在任何 store 初始化之前（store 会读桥接数据）
 *  ④ 注册 __haloHostEvent：必须在 systemInfo() 之前
 *     （宿主以 systemInfo() 的调用作为"前端就绪"信号，开始补推事件）
 *  ⑤ createPinia + mount：最后
 * ============ ============
 */
async function bootstrap(): Promise<void> {
  // ① 主题（同步部分）
  initTheme();

  // ② 全局错误处理
  installGlobalErrorHandlers();

  // ③ 桥接（必须 await：BindObjectAsync 是跨进程异步注册）
  await bindBridge();

  // ④ 事件桥
  registerHostEventBridge();

  // ⑤ 挂载
  const app = createApp(App);
  app.use(createAppPinia());
  // Vue 内部错误 → toast + console（与 utils/errors 的两层并列为三层兜底）
  app.config.errorHandler = (err, instance, info) => {
    console.error('[vue] 组件错误', info, err, instance);
  };
  app.mount('#app');

  // DEV-only：把传输层挂到 window，方便 Console 手动验证
  //（A-8.6：`transport.request('agent.prompt', …)` 一行触发 Mock 流式）。
  if (import.meta.env.DEV) {
    void import('./transport').then((m) => {
      (window as unknown as { __transport: unknown }).__transport = m.transport;
    });
  }

  // P5 验收工具：DEV 或 ?diag=1 时挂载 __p5diag。
  // 生产包无 diag 参数时不加载（动态 import，不进首屏产物）。
  if (import.meta.env.DEV || window.location.search.includes('diag=1')) {
    void import('./devtools/p5diag').then((m) => m.mountP5Diag());
  }
}

void bootstrap().catch((e) => {
  // 启动失败：显示一个最小可读的错误页（不能白屏）
  console.error('[bootstrap] 启动失败', e);
  renderFatalError(e);
});

function renderFatalError(e: unknown): void {
  const el = document.getElementById('app');
  if (!el) return;
  const msg = e instanceof Error ? `${e.name}: ${e.message}` : String(e);
  // @allow-innerhtml 启动失败兜底页（msg 已转义，无脚本注入面）
  el.innerHTML = `<!-- @allow-innerhtml -->
    <div style="padding:32px; /* @allow-hardcoded-color fatal fallback, CSS未加载时必须可读 */font:14px/1.6 'Microsoft YaHei',sans-serif;color:#333">
      <h1 style="font-size:18px;margin:0 0 12px">HaloAgent 界面启动失败</h1>
      <p style="color:#666 /* @allow-hardcoded-color */;margin:0 0 16px">
        请尝试：① 重启程序；② 如反复出现，运行「帮助 → 生成诊断报告」并联系技术支持。
      </p>
      <pre style="background:#f5f5f5 /* @allow-hardcoded-color */;padding:12px;border-radius:6px;overflow:auto;max-height:300px">${msg.replace(
        /[<>&]/g,
        (c) => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' })[c] ?? c
      )}</pre>
    </div>`;
}
