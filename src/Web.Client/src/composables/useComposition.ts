// src/Web.Client/src/composables/useComposition.ts
import { onBeforeUnmount, ref } from 'vue';

/**
 * ============ 为什么不是简单的 ref<boolean> ============
 *  坑 1 + 坑 2：某些 IME（Win7 微软拼音 2010）在“用 Enter 选词”时，
 *  keydown 的 isComposing === false，但 compositionend 还没到。
 *  如果我们只看 isComposing，就会把“选词的回车”当成“发送”。
 *
 *  解法：document 级监听 compositionstart / compositionend，
 *  并在 compositionend 后延迟 30ms 解除（覆盖“同帧”场景）。
 * =====================================================
 */
const END_DELAY_MS = 30;

export function useComposition() {
  const composing = ref(false);
  const startedAt = ref(0);
  let timer: number | null = null;

  function onStart(): void {
    if (timer !== null) {
      window.clearTimeout(timer);
      timer = null;
    }
    composing.value = true;
    startedAt.value = performance.now();
  }

  function onEnd(): void {
    if (timer !== null) window.clearTimeout(timer);
    timer = window.setTimeout(() => {
      composing.value = false;
      timer = null;
    }, END_DELAY_MS);
  }

  /**
   * 坑 3：input 事件在组合中也会触发。
   * 提供一个组合判定，供 input 处理使用（比 composing.value 更严格：
   * 若刚开始组合 < 5ms，视为“正在组合中”）。
   */
  function isComposingNow(e?: KeyboardEvent | InputEvent): boolean {
    if (composing.value) return true;
    if (e && 'isComposing' in e && e.isComposing) return true;
    return false;
  }

  document.addEventListener('compositionstart', onStart, true);
  document.addEventListener('compositionend', onEnd, true);

  onBeforeUnmount(() => {
    document.removeEventListener('compositionstart', onStart, true);
    document.removeEventListener('compositionend', onEnd, true);
    if (timer !== null) window.clearTimeout(timer);
  });

  return Object.assign(composing, { isComposingNow, startedAt });
}
