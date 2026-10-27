// src/Web.Client/src/utils/assert.ts
/**
 * 断言工具（开发期防呆，生产同样抛错、不静默）。
 */

/** 条件断言：不成立时抛错。 */
export function assert(cond: unknown, msg = '断言失败'): asserts cond {
  if (!cond) throw new Error(msg);
}

/** 非空断言：返回收窄后的值。 */
export function assertDefined<T>(v: T | null | undefined, msg = '值不应为空'): T {
  if (v === null || v === undefined) throw new Error(msg);
  return v;
}
