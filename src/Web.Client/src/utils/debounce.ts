// src/Web.Client/src/utils/debounce.ts
/**
 * 防抖：连续调用时只执行最后一次（等待 wait 毫秒无新调用后执行）。
 *
 * 典型用途：搜索框输入、窗口 resize 后的重排。
 */
export function debounce<A extends unknown[]>(
  fn: (...args: A) => void,
  wait: number
): (...args: A) => void {
  let timer: number | null = null;
  return (...args: A): void => {
    if (timer !== null) window.clearTimeout(timer);
    timer = window.setTimeout(() => {
      timer = null;
      fn(...args);
    }, wait);
  };
}
