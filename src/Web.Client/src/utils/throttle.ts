// src/Web.Client/src/utils/throttle.ts
/**
 * 节流：interval 毫秒内最多执行一次（前沿立即执行，尾沿补一次）。
 *
 * 典型用途：滚动/拖拽回调。注意 Splitter 拖拽要求跟手，
 * 不要在这里加过大的间隔（见验收 E-23）。
 */
export function throttle<A extends unknown[]>(
  fn: (...args: A) => void,
  interval: number
): (...args: A) => void {
  let last = 0;
  let timer: number | null = null;
  let pending: A | null = null;
  return (...args: A): void => {
    const now = Date.now();
    const remaining = interval - (now - last);
    pending = args;
    if (remaining <= 0) {
      if (timer !== null) {
        window.clearTimeout(timer);
        timer = null;
      }
      last = now;
      pending = null;
      fn(...args);
    } else if (timer === null) {
      timer = window.setTimeout(() => {
        timer = null;
        last = Date.now();
        const p = pending;
        pending = null;
        if (p) fn(...p);
      }, remaining);
    }
  };
}
