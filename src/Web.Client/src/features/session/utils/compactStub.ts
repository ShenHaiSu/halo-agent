import type { UnifiedChatMessage } from '@shared/bridge/protocol/domain';

/** P007：桩判定。有 kind 只认 kind；无 kind 的老数据走一次性前缀兼容。 */
export const LEGACY_STUB_PREFIX = '【上下文摘要';

export function isCompactStub(msg: UnifiedChatMessage | null | undefined): boolean {
  if (!msg) return false;
  const kind = String(msg.kind ?? '').trim();
  if (kind) return kind === 'compact_stub';
  return String(msg.role ?? '').toLowerCase() === 'user' && String(msg.content ?? '').startsWith(LEGACY_STUB_PREFIX);
}

/** P007：老桩文案清理，去掉写入侧旧前缀首行，只留摘要正文。 */
export function summaryBodyOf(msg: UnifiedChatMessage | null | undefined): string {
  const raw = String(msg?.content ?? '');
  if (!raw.startsWith(LEGACY_STUB_PREFIX)) return raw;
  const nl = raw.indexOf('\n');
  return nl >= 0 ? raw.slice(nl + 1) : raw;
}

export interface StubMeta {
  removedCount?: number;
  summaryTokens?: number;
  backupFile?: string | null;
}

/** P007：桩元数据解析，脏 JSON 返回空对象且不抛错。 */
export function metaOf(msg: UnifiedChatMessage | null | undefined): StubMeta {
  const raw = String(msg?.metaJson ?? '');
  if (!raw) return {};
  try {
    return JSON.parse(raw) as StubMeta;
  } catch {
    return {};
  }
}

/** P007：pill 文案，条数缺失时退化为不带条数。 */
export function stubLabel(removedCount: number | null | undefined): string {
  const n = Number(removedCount ?? 0);
  return n > 0 ? `上下文压缩 · ${n} 条 → 摘要` : '上下文压缩';
}

/** P007：导出分隔片段，前后端同文案。 */
export function dividerMarkdown(label: string, body: string, backupFile?: string | null): string {
  const lines = ['---', `> **${label}**${backupFile ? `（备份：${backupFile}）` : ''}`, '---', (body ?? '').trimEnd()];
  return lines.join('\n');
}
