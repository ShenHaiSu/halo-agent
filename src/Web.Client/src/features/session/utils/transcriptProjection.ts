/** bigChange P2-1: transcript projection — WeakMap snapshot + 64-block diff + history/tail split + generation guard. */
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { syncTranscriptProjection } from './transcriptCache';

export const BLOCK_SIZE = 64;

export interface TranscriptEntry {
  key: string;
  kind: 'user' | 'system' | 'turn';
  msg?: UnifiedChatMessage;
  groupKey?: string;
  assistantId?: string;
}

export interface TranscriptProjection {
  generation: number;
  history: TranscriptEntry[];
  tail: TranscriptEntry[];
  frozenAt: number;
}

interface SnapCache {
  generation: number;
  projection: TranscriptProjection;
  /** need23-R2：内容指纹（条数 + 首尾 id + 尾对象身份），数组浅拷贝但内容未变时可命中。 */
  msgCount: number;
  headId: string;
  tailId: string;
  tailRef: UnifiedChatMessage | null;
}

const snapCache = new WeakMap<UnifiedChatMessage[], SnapCache>();
/**
 * need23-R2：内容级缓存。按「末条消息对象身份」索引，与数组引用解耦。
 * 每次组装新数组（append / merge）都会产生新引用，但历史前缀的消息对象不变，
 * 因此末条对象相同 ⇒ 投影结果可整份复用。
 */
const tailCache = new Map<UnifiedChatMessage, SnapCache>();
const TAIL_CACHE_LIMIT = 24;
let generationCounter = 0;

/** need23-R2：只追加语义下的内容指纹（条数 + 首 id + 尾 id）。 */
function fingerprintOf(messages: UnifiedChatMessage[]): [number, string, string, UnifiedChatMessage | null] {
  const n = messages.length;
  if (!n) return [0, '', '', null];
  return [n, String(messages[0]?.id || ''), String(messages[n - 1]?.id || ''), messages[n - 1]];
}

function entryOf(m: UnifiedChatMessage): TranscriptEntry {
  const role = String(m.role || '').toLowerCase();
  if (role === 'user') return { key: `u-${m.id}`, kind: 'user', msg: m };
  if (role === 'assistant' || role === 'tool')
    return { key: `t-${m.id}`, kind: 'turn', msg: m, assistantId: String(m.id), groupKey: String(m.id) };
  return { key: `s-${m.id}`, kind: 'system', msg: m };
}

/** 64-block diff: reuse history refs block-wise; only tail rebuilt.
 * need23-R2 命中条件：数组引用相同（WeakMap）或 内容指纹一致（末条对象身份 Map）——后者覆盖浅拷贝/新数组场景。 */
export function projectTranscript(messages: UnifiedChatMessage[], tailSize = 8): TranscriptProjection {
  const [msgCount, headId, tailId, tailRef] = fingerprintOf(messages);
  const same = (hit: SnapCache | undefined): boolean =>
    !!hit && hit.msgCount === msgCount && hit.headId === headId && hit.tailId === tailId && hit.tailRef === tailRef;
  const hit = snapCache.get(messages);
  if (same(hit)) return (hit as SnapCache).projection;
  const byTail = tailRef ? tailCache.get(tailRef) : undefined;
  if (same(byTail)) {
    const p = (byTail as SnapCache).projection;
    // 把新数组也登记到 WeakMap，后续同引用直接命中
    snapCache.set(messages, byTail as SnapCache);
    return p;
  }
  const history: TranscriptEntry[] = [];
  const splitAt = Math.max(0, messages.length - tailSize);
  for (let b = 0; b < splitAt; b += BLOCK_SIZE) {
    const end = Math.min(splitAt, b + BLOCK_SIZE);
    for (let i = b; i < end; i++) history.push(entryOf(messages[i]));
  }
  const tail: TranscriptEntry[] = [];
  for (let i = splitAt; i < messages.length; i++) tail.push(entryOf(messages[i]));
  const projection: TranscriptProjection = {
    generation: ++generationCounter,
    history,
    tail,
    frozenAt: Date.now(),
  };
  const snap: SnapCache = {
    generation: projection.generation,
    projection,
    msgCount,
    headId,
    tailId,
    tailRef,
  };
  snapCache.set(messages, snap);
  if (tailRef) {
    tailCache.delete(tailRef);
    tailCache.set(tailRef, snap);
    while (tailCache.size > TAIL_CACHE_LIMIT) {
      const oldest = tailCache.keys().next();
      if (oldest.done) break;
      tailCache.delete(oldest.value);
    }
  }
  return projection;
}

/** Incremental path: history refs reused when prefix unchanged; tail rebuilt. */
export function incremental(
  prev: TranscriptProjection | null,
  prevMessages: UnifiedChatMessage[],
  nextMessages: UnifiedChatMessage[],
  tailSize = 8
): TranscriptProjection {
  if (!prev) return replaceMessages(nextMessages, tailSize);
  let prefixLen = 0;
  const max = Math.min(prevMessages.length, nextMessages.length);
  while (prefixLen < max && prevMessages[prefixLen].id === nextMessages[prefixLen].id) prefixLen++;
  const next = projectTranscript(nextMessages, tailSize);
  // Reuse previous history entry objects for the unchanged prefix blocks.
  const reuseCount = Math.min(prefixLen, prev.history.length, next.history.length);
  for (let i = 0; i < reuseCount; i++) next.history[i] = prev.history[i];
  return next;
}

/** Full replace with generation guard: stale generations are ignored by callers via checkGeneration. */
export function replaceMessages(messages: UnifiedChatMessage[], tailSize = 8): TranscriptProjection {
  return projectTranscript(messages, tailSize);
}

export function checkGeneration(p: TranscriptProjection, expected: number): boolean {
  return p.generation === expected;
}

/** Canonical write boundary: history/tail memo + pane cache sync. historyEntries memo frozen, tail live. */
export function mergeLiveMessages(
  persisted: UnifiedChatMessage[],
  live: TranscriptEntry[]
): { full: TranscriptProjection; history: TranscriptEntry[]; tail: TranscriptEntry[] } {
  const full = projectTranscript(persisted);
  const tail: TranscriptEntry[] = [...full.tail, ...live];
  syncTranscriptProjection(full.generation, full.history, tail);
  return { full, history: full.history, tail };
}
