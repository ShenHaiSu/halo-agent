// src/Web.Client/src/composables/useAutoResize.ts
import { nextTick, onMounted, watch, type Ref } from 'vue';

/**
 * textarea 自增高（1~12 行，见 02 §6.4）。
 *
 * ============ 用手动测量而不是 field-sizing ============
 *  CSS 的 field-sizing: content 是 Chrome 123+，
 *  check:compat 会拦（我们明确禁用）。
 *  所以：把 height 设为 auto → 读 scrollHeight → 设为 min(scrollHeight, max)。
 *  必须设 overflow-hidden 再测，否则 scrollHeight 会含滚动条。
 * ==========================================================
 */
export function useAutoResize(
  el: Ref<HTMLTextAreaElement | null>,
  value: Ref<string>,
  opts: { minRows: number; maxRows: number }
): void {
  function resize(): void {
    const t = el.value;
    if (!t) return;

    const cs = getComputedStyle(t);
    const lineHeight = parseFloat(cs.lineHeight) || 20;
    const paddingY = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom);
    const borderY = parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);

    const minH = lineHeight * opts.minRows + paddingY + borderY;
    const maxH = lineHeight * opts.maxRows + paddingY + borderY;

    t.style.height = 'auto';
    t.style.overflowY = 'hidden';
    const next = Math.min(Math.max(t.scrollHeight + borderY, minH), maxH);
    t.style.height = `${next}px`;
    t.style.overflowY = t.scrollHeight + borderY > maxH ? 'auto' : 'hidden';
  }

  onMounted(() => void nextTick(resize));
  watch(value, () => void nextTick(resize));
}
