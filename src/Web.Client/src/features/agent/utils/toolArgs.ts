import type { AgentToolCard } from '@features/agent/stores/model';

/**
 * 工具参数解析唯一入口：容错 JSON 解析 + 按调用标识记忆化。
 * 解析失败回空对象并告警，调用方按无参数渲染，不抛错。
 */
const ARGS_CACHE_MAX = 100;
const argsParseCache = new Map<string, Record<string, unknown>>();

function hashStr(s: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(16);
}

export function parseToolArgs(card: Pick<AgentToolCard, 'argsJson' | 'callId'>): Record<string, unknown> {
  const raw = card.argsJson || '{}';
  const key = `${card.callId}:${hashStr(raw)}:${raw.length}`;
  const hit = argsParseCache.get(key);
  if (hit) return hit;
  let out: Record<string, unknown> = {};
  try {
    const parsed: unknown = JSON.parse(raw);
    if (parsed && typeof parsed === 'object') out = parsed as Record<string, unknown>;
  } catch (e) {
    console.warn('[tool-card] args parse failed:', card.callId, e);
  }
  argsParseCache.set(key, out);
  if (argsParseCache.size > ARGS_CACHE_MAX) {
    const oldest = argsParseCache.keys().next();
    if (!oldest.done) argsParseCache.delete(oldest.value);
  }
  return out;
}

/** 单测隔离：清空参数解析缓存。 */
export function clearToolArgsCache(): void {
  argsParseCache.clear();
}

/** 按优先级取首个非空字符串参数。 */
export function stringArg(args: Record<string, unknown>, ...keys: string[]): string {
  for (const k of keys) {
    const v = args[k];
    if (typeof v === 'string' && v) return v;
  }
  return '';
}

/** 执行命令原文（缺省回占位，模板直接渲染）。 */
export function execCommandOf(args: Record<string, unknown>): string {
  return stringArg(args, 'command') || '(空命令)';
}

/** 折叠态单行截断（模板做视觉省略，title 悬停看全文）。 */
export function execShortOf(command: string, max = 80): string {
  const t = String(command ?? '')
    .replace(/\s+/g, ' ')
    .trim();
  return t.length > max ? t.slice(0, max) + '…' : t;
}
