// src/Web.Client/src/composables/useShortcuts.ts
import { onBeforeUnmount, onMounted } from 'vue';

/**
 * 应用内快捷键（见 01 §11.3 冲突处理原则）。
 *
 * v3：OS 级 RegisterHotKey 已拆除。本文件仅在主窗口激活获焦时生效，
 * 失焦/最小化/切到别的程序一律不响应（document.hasFocus + focus/blur 使能位）。
 *
 * ============ 为什么在 document 上监听而不是 window ============
 *  CefSharp 的 window 层与 document 层在 capture 阶段行为一致，
 *  但 document 更早拿到事件（捕获阶段顺序：window → document → ...），
 *  能更可靠地 preventDefault。
 * ============ 为什么用 capture: true ============
 *  某些组件（HMenu）会在冒泡阶段 stopPropagation，
 *  若我们监听冒泡阶段就会收不到。capture 阶段无法被 stopPropagation 阻止。
 * ==========================================================
 *
 * ============ 为什么是依赖注入而不是直接 import stores ============
 *  composables 目录被 check-layering 禁止 import stores/transport。
 *  调用方（App 层，允许 import stores）传入回调，本函数只做按键分发。
 * ==========================================================
 */
export interface ShortcutHandlers {
  /** IME 组合中（任一为真即视为组合中） */
  isComposing: () => boolean;
  /** 是否有模态/弹层打开（设置页等） */
  hasOpenModal: () => boolean;
  closeModal: () => void;
  openSettings: () => void;
  /** 聚焦会话搜索（命令面板落点） */
  focusSearch: () => void;
  /** 显示快捷键帮助 */
  showShortcutHelp: () => void;
  /** 新建会话 */
  newSession: () => void;
  /** 生成中 */
  isBusy: () => boolean;
  /** 中断生成 */
  cancelTurn: () => void;
}

export function useShortcuts(h: ShortcutHandlers): void {
  let windowActive = true;

  function onFocus(): void {
    windowActive = true;
  }
  function onBlur(): void {
    windowActive = false;
  }

  function onKeydown(e: KeyboardEvent): void {
    // 仅主窗口激活获焦时响应；失焦一律忽略。
    if (!windowActive) return;
    try {
      if (typeof document !== 'undefined' && 'hasFocus' in document && !document.hasFocus()) return;
    } catch {
      /* 忽略 */
    }
    // P-2：IME 优先（除 Esc 交给输入法取消组合）
    if ((e.isComposing || h.isComposing()) && e.key !== 'Escape') return;

    const mod = e.ctrlKey || e.metaKey;
    const key = e.key.toLowerCase();

    // P-4：显式阻止浏览器默认（F5 刷新会丢状态，F7 插入符浏览）
    if (['f5', 'f7'].includes(key)) {
      e.preventDefault();
      return;
    }
    // N/P/O/F/G/J/U：删除无条件全局捕获，改为获焦门内应用键。
    // N=新建会话；其余仅 preventDefault，不再裸奔到 CEF 默认；S 只拦截不保存（保存由资源页统一分发）。
    if (mod && ['n', 'p', 's', 'o', 'f', 'g', 'j', 'u'].includes(key)) {
      e.preventDefault();
      if (key === 'n') h.newSession();
      return;
    }

    if (mod && key === 'k') {
      e.preventDefault();
      h.focusSearch();
      return;
    }
    if (mod && key === ',') {
      e.preventDefault();
      h.openSettings();
      return;
    }
    if (mod && key === '/') {
      e.preventDefault();
      h.showShortcutHelp();
      return;
    }
    if (key === 'escape') {
      if (h.hasOpenModal()) {
        h.closeModal();
        return;
      }
      if (h.isBusy()) {
        h.cancelTurn();
        return;
      }
    }
  }

  onMounted(() => {
    try {
      windowActive = typeof document === 'undefined' || !('hasFocus' in document) || document.hasFocus();
    } catch {
      windowActive = true;
    }
    window.addEventListener('focus', onFocus);
    window.addEventListener('blur', onBlur);
    document.addEventListener('keydown', onKeydown, { capture: true });
  });
  onBeforeUnmount(() => {
    window.removeEventListener('focus', onFocus);
    window.removeEventListener('blur', onBlur);
    document.removeEventListener('keydown', onKeydown, { capture: true });
  });
}
