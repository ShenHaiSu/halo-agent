// src/Web.Client/src/utils/cn.ts
/**
 * 合并 class 名（过滤 falsy）。
 *
 * ============ 为什么不用 clsx ============
 *  clsx 的核心逻辑就是 `filter(Boolean).join(' ')`，
 *  3 行就能写完。引一个包只为了这个不值得。
 *
 *  注意：我们**不做** tailwind-merge（去冲突）。
 *  理由：
 *    ① tailwind-merge 有 ~10KB，且规则维护滞后于 Tailwind 版本
 *    ② 我们的组件用数组拼 class，冲突本身就少
 *    ③ 真需要覆盖时，用 CSS 优先级或加 !important 更可控
 * ============ ============
 */
export type ClassValue =
  | string
  | number
  | null
  | undefined
  | false
  | ClassValue[]
  | Record<string, boolean | undefined | null>;

export function cn(...args: ClassValue[]): string {
  const out: string[] = [];

  const walk = (v: ClassValue): void => {
    if (v === null || v === undefined || v === false || v === '') return;
    if (typeof v === 'string') {
      out.push(v);
      return;
    }
    if (typeof v === 'number') {
      if (v !== 0) out.push(String(v));
      return;
    }
    if (Array.isArray(v)) {
      for (const item of v) walk(item);
      return;
    }
    for (const [k, on] of Object.entries(v)) {
      if (on) out.push(k);
    }
  };

  for (const a of args) walk(a);
  return out.join(' ');
}
