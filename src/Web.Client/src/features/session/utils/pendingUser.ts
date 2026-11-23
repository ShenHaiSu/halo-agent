// need02-P1：乐观 user 纯函数（与 store 解耦，可单测；禁止调 bridge/DOM）。
// 语义来源：docs/need-02/03 §3.1 + 04-P1 §对账规则 + 05 R2–R4。
import type { ChatImageRef } from '@shared/bridge/hostBridge';

export type PendingStatus = 'sending' | 'confirmed' | 'failed' | 'superseded';

export interface PendingUser {
  /** 本地键：`pending-<requestId>`（D2=A）。回包确认后原位替换为真实 id，timeline key 不变。 */
  key: string;
  requestId: string;
  sessionId: string;
  /** 净文本（含 [image:attId] 占位，原样存）。 */
  content: string;
  /** 本地存根：imageId=attId，转正后换真实 imageId。 */
  images: ChatImageRef[];
  /** 本地预览：imageId -> dataURL（base64），pending 期直接渲染，不走缩略图懒取。 */
  previews: Record<string, string>;
  /** 本地 now（ISO），对账后以后端时间为准。 */
  createdAtUtc: string;
  status: PendingStatus;
  errorCode?: string | null;
  /** 命中后的真实消息 id（调试/对账用，渲染 key 仍用 pending.key）。 */
  confirmedId?: string | null;
}

export function makePendingKey(requestId: string): string {
  return `pending-${String(requestId || '').trim()}`;
}

export function makePendingUser(args: {
  requestId: string;
  sessionId: string;
  content: string;
  images?: ChatImageRef[];
  previews?: Record<string, string>;
  createdAtUtc?: string;
}): PendingUser {
  const requestId = String(args.requestId || '').trim();
  const sessionId = String(args.sessionId || '').trim();
  return {
    key: makePendingKey(requestId),
    requestId,
    sessionId,
    content: String(args.content ?? ''),
    images: Array.isArray(args.images) ? args.images.slice() : [],
    previews: args.previews ? { ...args.previews } : {},
    createdAtUtc: args.createdAtUtc || new Date().toISOString(),
    status: 'sending',
    errorCode: null,
    confirmedId: null,
  };
}

/** pending 是否属于该会话（切会话隔离：只 overlay 源 session，切走即藏）。 */
export function isPendingForSession(pending: PendingUser | null | undefined, sessionId: string): boolean {
  if (!pending) return false;
  const sid = String(sessionId || '').trim();
  if (!sid) return false;
  return String(pending.sessionId || '').trim() === sid;
}

/** pending 是否超时未确认（30s 兜底：仍显示，标 `?`，下次全量拉再试）。 */
export function isPendingExpired(pending: PendingUser | null | undefined, nowMs: number = Date.now(), timeoutMs = 30_000): boolean {
  if (!pending || pending.status !== 'sending') return false;
  const t = Date.parse(String(pending.createdAtUtc || ''));
  if (!Number.isFinite(t)) return false;
  return nowMs - t > timeoutMs;
}

/** 摘要源精确对账：pending.requestId 与 turn.lastRequestId 一致即命中（v2 下 jsonl 为空时的主对账路径）。 */
export function matchConfirmedBySummary(
  pending: PendingUser | null | undefined,
  summaries: Array<{ lastRequestId?: string | null }> | null | undefined
): { lastRequestId: string } | null {
  if (!pending || pending.status !== 'sending') return null;
  const reqId = String(pending.requestId || '').trim();
  if (!reqId) return null;
  const list = Array.isArray(summaries) ? summaries : [];
  for (let i = list.length - 1; i >= 0; i--) {
    const s = list[i] as { lastRequestId?: unknown } | null | undefined;
    if (s && String((s as { lastRequestId?: unknown }).lastRequestId || '').trim() === reqId) {
      return s as { lastRequestId: string };
    }
  }
  return null;
}

/** pending 期缩略图直出（本地 dataURL），转正后走正常懒取。 */
export function pendingPreviewOf(pending: PendingUser | null | undefined, imageId: string): string {
  if (!pending) return '';
  const id = String(imageId || '').trim();
  if (!id) return '';
  return String(pending.previews?.[id] || '');
}
