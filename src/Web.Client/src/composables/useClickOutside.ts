// src/Web.Client/src/composables/useClickOutside.ts
import { onBeforeUnmount, watch, type Ref } from 'vue';

interface Options {
  /** 这些元素内的点击不算"外部"（如触发按钮） */
  ignore?: Array<Ref<HTMLElement | null>>;
  /** 用 mousedown 而不是 click（更即时，避免与内部按钮冲突） */
  event?: 'mousedown' | 'click';
  /** 忽略右键 */
  ignoreRightButton?: boolean;
}

/**
 * 点击元素外部时执行回调。
 *
 * ============ 三个必须处理的细节 ============
 *  ① 用 capture 阶段监听：有些内部元素会 stopPropagation，
 *     用冒泡阶段会漏掉"点了外部某个 stopPropagation 的元素"
 *  ② ignore 列表：Popover 的触发按钮通常也在"外部"范围内，
 *     点它应该是"切换"而不是"关闭再打开"
 *  ③ 面板消失时立即移除监听（避免泄漏）
 * ============ 为什么默认用 mousedown ============
 *  click 在 mousedown → mouseup 之后触发。
 *  如果用户在面板里按住鼠标拖到外面再松开，
 *  click 事件会落在面板外的共同父元素上，导致面板被误关。
 *  mousedown 没有这个问题。
 * ============ ============
 */
export function useClickOutside(
  target: Ref<HTMLElement | null>,
  handler: (e: Event) => void,
  options: Options = {}
): void {
  const { ignore = [], event = 'mousedown', ignoreRightButton = true } = options;

  let attached = false;

  function isInside(el: HTMLElement | null, node: Node | null): boolean {
    if (!el || !node) return false;
    return el === node || el.contains(node);
  }

  function onEvent(e: Event) {
    if (ignoreRightButton && e instanceof MouseEvent && e.button !== 0) return;

    const node = e.target as Node | null;
    if (isInside(target.value, node)) return;
    for (const ig of ignore) {
      if (isInside(ig.value, node)) return;
    }
    handler(e);
  }

  function attach() {
    if (attached) return;
    document.addEventListener(event, onEvent, true);
    attached = true;
  }

  function detach() {
    if (!attached) return;
    document.removeEventListener(event, onEvent, true);
    attached = false;
  }

  watch(
    target,
    (el) => {
      if (el) attach();
      else detach();
    },
    { immediate: true, flush: 'post' }
  );

  onBeforeUnmount(detach);
}

/** 别名：文档中 HMenu/HPopover 使用 onClickOutside 名称 */
export { useClickOutside as onClickOutside };
