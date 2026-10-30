// src/Web.Client/src/components/chat/toolArgs.ts

/**
 * ============ 参数摘要的规则 ============
 *  卡片头部只能显示一行。优先显示“最能说明意图”的参数：
 *    read_file / write_file / edit_file → path
 *    run_shell → command（截断）
 *    grep → pattern + path
 *    glob → pattern
 *    其他 → 第一个字符串参数
 *  JSON 解析失败 → 显示原文截断（不报错）。
 *  单行输出，上限 80 字符。
 * ==========================================
 */
const PRIORITY: Record<string, string[]> = {
  read_file: ['path', 'file_path'],
  write_file: ['path', 'file_path'],
  edit_file: ['path', 'file_path'],
  delete_file: ['path', 'file_path'],
  list_directory: ['path', 'dir'],
  run_shell: ['command', 'cmd'],
  grep: ['pattern', 'path'],
  glob: ['pattern', 'path'],
  web_fetch: ['url'],
  web_search: ['query', 'q'],
};

const MAX_SUMMARY = 80;

export function summarizeArgs(name: string, rawArgs: string, kind?: string): string {
  let obj: Record<string, unknown> | null = null;
  try {
    const parsed: unknown = JSON.parse(rawArgs || '{}');
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      obj = parsed as Record<string, unknown>;
    }
  } catch {
    /* 解析失败：原文截断 */
  }

  if (!obj) return truncate(rawArgs || '（无参数）', MAX_SUMMARY);

  // unknown 工具短路：不走名称映射（否则如 web_search 会命中中文“联网搜索”并与“标题=rawType”矛盾）。
  // 分流键优先级：kind 优先于 name。
  const keys = kind === 'unknown' ? [] : (PRIORITY[name] ?? []);
  for (const k of keys) {
    const v = obj[k];
    if (typeof v === 'string' && v.trim()) return truncate(v, MAX_SUMMARY);
  }
  // 兜底：第一个字符串值
  for (const v of Object.values(obj)) {
    if (typeof v === 'string' && v.trim()) return truncate(v, MAX_SUMMARY);
  }
  // 只有非字符串参数
  const firstKey = Object.keys(obj)[0];
  if (!firstKey) return '（无参数）';
  return truncate(`${firstKey}=${JSON.stringify(obj[firstKey])}`, MAX_SUMMARY);
}

function truncate(s: string, max: number): string {
  const one = s.replace(/\s+/g, ' ').trim();
  return one.length <= max ? one : one.slice(0, max - 1) + '…';
}
