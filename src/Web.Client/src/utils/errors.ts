// src/Web.Client/src/utils/errors.ts
// D4-8 全局错误处理：三层兜底（onerror / unhandledrejection / Vue errorHandler 在 main.ts 装）。
import { useToast } from '@/composables/useToast';

/**
 * 全局错误处理。
 *
 * ============ 三层兜底 ============
 *  ① window.onerror（未捕获的同步错误）
 *  ② unhandledrejection（未处理的 Promise 拒绝）
 *  ③ Vue 的 app.config.errorHandler（Vue 内部错误）→ 在 main.ts 里装
 *
 *  三者都要：toast 提示用户 + console.error 留证据。
 *  绝不静默。
 * ============ ============
 */
export function installGlobalErrorHandlers(): void {
  const toast = useToast();

  window.addEventListener('error', (e) => {
    // 资源加载失败（img/script）不会进这里，只有脚本异常会
    if (!e.message) return;
    console.error('[global] 未捕获错误', e.error ?? e.message);
    toast.error(`界面出错：${e.message}`);
  });

  window.addEventListener('unhandledrejection', (e) => {
    const reason = e.reason;
    const msg = reason instanceof Error ? reason.message : String(reason ?? '未知错误');
    console.error('[global] 未处理的 Promise 拒绝', reason);
    // 桥接未就绪这类"预期内"的错误不打扰用户
    if (msg.includes('引擎桥接不可用') || msg.includes('宿主桥接不可用')) return;
    toast.error(`操作失败：${msg}`);
  });
}

/** 把任意异常转成可显示的文本 */
export function toMessage(e: unknown): string {
  if (e instanceof Error) return e.message;
  if (typeof e === 'string') return e;
  if (e && typeof e === 'object' && 'message' in e) {
    return String((e as { message: unknown }).message);
  }
  return String(e);
}
