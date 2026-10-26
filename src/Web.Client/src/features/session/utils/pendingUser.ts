// need02-P1：乐观 user 纯函数（与 store 解耦，可单测；禁止调 bridge/DOM）。
// 语义来源：docs/need-02/03 §3.1 + 04-P1 §对账规则 + 05 R2–R4。
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';

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
export function isPendingExpired(
  pending: PendingUser | null | undefined,
  nowMs: number = Date.now(),
  timeoutMs = 30_000,
): boolean {
  if (!pending || pending.status !== 'sending') return false;
  const t = Date.parse(String(pending.createdAtUtc || ''));
  if (!Number.isFinite(t)) return false;
  return nowMs - t > timeoutMs;
}

function tailUsers(messages: UnifiedChatMessage[], n = 5): UnifiedChatMessage[] {
  const out: UnifiedChatMessage[] = [];
  for (let i = messages.length - 1; i >= 0 && out.length < n; i--) {
    const m = messages[i];
    if (!m) continue;
    if (String((m as UnifiedChatMessage).role || '').toLowerCase() !== 'user') continue;
    out.push(m);
  }
  return out;
}

function withinWindow(aIso: string, bIso: string, ms = 60_000): boolean {
  const a = Date.parse(String(aIso || ''));
  const b = Date.parse(String(bIso || ''));
  if (!Number.isFinite(a) || !Number.isFinite(b)) return true;
  return Math.abs(a - b) <= ms;
}

/**
 * 对账（启发式 + 精确式双兼容，纯函数）：
 * 1. 精确：后端若回带 `requestId`（07 可选 B1），直接按 requestId 匹配；
 * 2. 启发：尾部 5 条内找 `role=user && content==pending.content && createdAt ±60s`；
 * 3. 兜底由调用方做（30s 标 `?`，见 isPendingExpired），这里只返回命中或 null。
 */
export function matchConfirmed(
  pending: PendingUser | null | undefined,
  messages: UnifiedChatMessage[] | null | undefined,
): UnifiedChatMessage | null {
  if (!pending) return null;
  // failed/superseded/confirmed 不再参与对账（R4：重发新 requestId，旧 pending 不复用）。
  if (pending.status !== 'sending') return null;
  const list = Array.isArray(messages) ? messages : [];
  if (!list.length) return null;
  const reqId = String(pending.requestId || '').trim();
  if (reqId) {
    for (let i = list.length - 1; i >= 0; i--) {
      const m = list[i] as unknown as { requestId?: unknown; id?: unknown };
      if (m && String((m as { requestId?: unknown }).requestId || '').trim() === reqId) {
        return list[i];
      }
    }
  }
  const want = String(pending.content ?? '');
  for (const m of tailUsers(list, 5)) {
    if (String(m.content ?? '') !== want) continue;
    if (!withinWindow(String(pending.createdAtUtc || ''), String((m as UnifiedChatMessage).createdAtUtc || ''), 60_000)) {
      continue;
    }
    // 图片占位一致性：pending 有图时要求候选至少携带同数图片（避免纯文本误命中带图轮）。
    if (pending.images.length > 0) {
      const imgs = Array.isArray((m as UnifiedChatMessage).images) ? (m as UnifiedChatMessage).images!.length : 0;
      if (imgs < pending.images.length) continue;
    }
    return m;
  }
  return null;
}

/**
 * overlay 用合成消息（仅视图层，不写 jsonl）：
 * id = pending.key（timeline key `u-<key>` 全程稳定，DOM 不重建）；
 * images 原样带本地存根 + previews 供 thumb 直出。
 */
export function toPendingMessage(pending: PendingUser): UnifiedChatMessage {
  return {
    id: pending.key,
    role: 'user',
    content: pending.content,
    reasoningContent: null,
    toolCalls: null,
    toolCallId: null,
    name: null,
    inputTokens: null,
    outputTokens: null,
    cachedTokens: null,
    createdAtUtc: pending.createdAtUtc,
    images: pending.images.slice(),
  } as UnifiedChatMessage;
}

/** pending 期缩略图直出（本地 dataURL），转正后走正常懒取。 */
export function pendingPreviewOf(pending: PendingUser | null | undefined, imageId: string): string {
  if (!pending) return '';
  const id = String(imageId || '').trim();
  if (!id) return '';
  return String(pending.previews?.[id] || '');
}
