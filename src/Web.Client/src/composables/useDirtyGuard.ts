// src/Web.Client/src/composables/useDirtyGuard.ts
// 未保存改动离开拦截（01 §9.2 + 04 §7.3）。
import { ref, type Ref } from 'vue';

/**
 * ============ 为什么不用 window.confirm ============
 *  CefSharp 的 window.confirm 会阻塞 UI 线程，导致引擎心跳超时被误判为卡死。
 *  所以离开拦截一律用 HDialog（dialogOpen 由调用方渲染）。
 * ============ 三种离开路径都要拦 ============
 *  ① 切设置分页 ② 关设置页 ③ 关应用（由 SettingsApp 统一经 confirmLeave）
 * ============ 不做自动保存的理由 ============
 *  settings.save* 会写 config.json；每敲一字符写一次会导致中间态被引擎读到。
 * ==========================================
 */
export function useDirtyGuard(dirty: Ref<boolean>, onDiscard: () => void) {
  const dialogOpen = ref(false);
  let pending: (() => void) | null = null;

  /** 想离开时先走这里：无脏直接执行，有脏弹确认。 */
  function confirmLeave(action: () => void): void {
    if (!dirty.value) {
      action();
      return;
    }
    pending = action;
    dialogOpen.value = true;
  }

  /** 用户在确认框点“放弃改动”：先重置表单，再执行挂起的离开动作。 */
  function confirmDiscard(): void {
    dialogOpen.value = false;
    onDiscard();
    const a = pending;
    pending = null;
    a?.();
  }

  function cancelLeave(): void {
    pending = null;
    dialogOpen.value = false;
  }

  return { dialogOpen, confirmLeave, confirmDiscard, cancelLeave };
}

export type DirtyGuard = ReturnType<typeof useDirtyGuard>;
