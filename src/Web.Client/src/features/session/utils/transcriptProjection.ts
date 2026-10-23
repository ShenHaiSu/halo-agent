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
}

const snapCache = new WeakMap<UnifiedChatMessage[], SnapCache>();
let generationCounter = 0;

function entryOf(m: UnifiedChatMessage): TranscriptEntry {
  const role = String(m.role || '').toLowerCase();
  if (role === 'user') return { key: `u-${m.id}`, kind: 'user', msg: m };
  if (role === 'assistant' || role === 'tool')
    return { key: `t-${m.id}`, kind: 'turn', msg: m, assistantId: String(m.id), groupKey: String(m.id) };
  return { key: `s-${m.id}`, kind: 'system', msg: m };
}

/** 64-block diff: reuse history refs block-wise; only tail rebuilt. Returns cached projection when input array ref unchanged. */
export function projectTranscript(messages: UnifiedChatMessage[], tailSize = 8): TranscriptProjection {
  const hit = snapCache.get(messages);
  if (hit) return hit.projection;
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
  snapCache.set(messages, { generation: projection.generation, projection });
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
