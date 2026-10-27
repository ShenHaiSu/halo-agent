// src/Web.Client/src/utils/id.ts
/**
 * 轻量唯一 id（进程内单调递增 + 时间前缀）。
 *
 * 不用 crypto.randomUUID：计数器在同一次会话内可读性更好
 * （调试时一眼看出创建顺序），且零兼容性顾虑。
 */

let seq = 0;

/** 生成一个稳定唯一 id（用于 :key 与滚动定位）。 */
export function nextId(prefix = 'id'): string {
  seq += 1;
  return `${prefix}-${Date.now().toString(36)}-${seq.toString(36)}`;
}
