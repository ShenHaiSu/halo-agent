// src/Web.Client/src/composables/useShortcuts.ts
import { onBeforeUnmount, onMounted } from 'vue';

/**
 * 全局快捷键（见 01 §11.3 冲突处理原则）。
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
 *  行为与 01 §11.3 一致：capture、IME 守卫、Ctrl+K/,/N preventDefault、
 *  Esc 关闭弹层/设置/取消。
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
  function onKeydown(e: KeyboardEvent): void {
    // P-2：IME 优先（除 Esc 交给输入法取消组合）
    if ((e.isComposing || h.isComposing()) && e.key !== 'Escape') return;

    const mod = e.ctrlKey || e.metaKey;
    const key = e.key.toLowerCase();

    // P-4：显式阻止浏览器默认
    if (['f5', 'f7'].includes(key)) {
      e.preventDefault();
      return;
    }
    if (mod && ['n', 'p', 's', 'o', 'f', 'g', 'j', 'u'].includes(key)) {
      // Ctrl+S 交给输入框/编辑器自己处理（保存），其余阻止
      if (key !== 's') e.preventDefault();
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

  onMounted(() => document.addEventListener('keydown', onKeydown, { capture: true }));
  onBeforeUnmount(() => document.removeEventListener('keydown', onKeydown, { capture: true }));
}
