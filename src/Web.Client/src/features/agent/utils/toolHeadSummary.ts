/**
 * 通用工具头摘要：让所有 tool 折叠态中间都有信息展示。
 * 特例（read/list/grep/view/exec）仍由 ToolCallCard 专属文案负责，这里只服务 default 分支。
 */

const SHORT_KEYS = ['action', 'name', 'scope', 'title', 'milestone', 'task', 'label', 'pattern', 'query', 'path', 'command'] as const;

const BIG_KEYS = new Set(['content', 'body', 'new_string', 'old_string', 'description', 'milestones', 'text', 'output']);

const HEAD_MAX = 120;
const VALUE_MAX = 40;

function singleLine(s: string): string {
  return String(s ?? '')
    .replace(/\s+/g, ' ')
    .trim();
}

function shortVal(v: unknown): string {
  if (typeof v === 'string') {
    const t = v.trim();
    return t.length > VALUE_MAX ? t.slice(0, VALUE_MAX) + '…' : t;
  }
  if (typeof v === 'number' || typeof v === 'boolean') return String(v);
  try {
    const j = JSON.stringify(v) ?? '?';
    const t = singleLine(j);
    return t.length > VALUE_MAX ? t.slice(0, VALUE_MAX) + '…' : t;
  } catch {
    return '?';
  }
}

/**
 * 通用摘要：优先短标量键 `k=v · …`，无短键则 `{k:v, …}`，半包 JSON 时回落 raw 切片。
 * 恒返回单行短串（空参返回 ''，由调用方决定是否兜底 '(无参数)'）。
 */
export function summarizeToolHead(args: Record<string, unknown>, rawJson?: string): string {
  const a = args && typeof args === 'object' ? args : {};
  const parts: string[] = [];
  for (const k of SHORT_KEYS) {
    const v = (a as Record<string, unknown>)[k];
    if (typeof v === 'string') {
      if (!v.trim()) continue;
      parts.push(`${k}=${shortVal(v)}`);
    } else if (typeof v === 'number' || typeof v === 'boolean') {
      parts.push(`${k}=${String(v)}`);
    }
    if (parts.join(' · ').length >= 100) break;
  }
  if (parts.length > 0) {
    const s = singleLine(parts.join(' · '));
    return s.length > HEAD_MAX ? s.slice(0, HEAD_MAX) + '…' : s;
  }
  const keys = Object.keys(a)
    .filter((k) => !BIG_KEYS.has(k))
    .slice(0, 3);
  if (keys.length > 0) {
    const s = singleLine(`{${keys.map((k) => `${k}:${shortVal(a[k])}`).join(', ')}}`);
    return s.length > HEAD_MAX ? s.slice(0, HEAD_MAX) + '…' : s;
  }
  const raw = singleLine(String(rawJson ?? ''));
  if (raw && raw !== '{}') {
    return raw.length > 80 ? raw.slice(0, 80) + '…' : raw;
  }
  return '';
}
