/** bigChange P2-1: transcript LRU cache — Map LRU LIMIT=20 + retained pane LIMIT=3. Sole write boundary syncTranscriptProjection. */
export const TRANSCRIPT_CACHE_LIMIT = 20;
export const RETAINED_PANE_LIMIT = 3;

interface CacheRow {
  generation: number;
  history: unknown[];
  tail: unknown[];
  at: number;
}

const lru = new Map<string, CacheRow>();
const retainedPanes = new Map<string, number>();

function paneKeyOf(generation: number): string {
  return `gen:${generation}`;
}

/** Unique write boundary for transcript projection (唯一调用方：useTimeline.ts 的 historyEntries 投影). */
export function syncTranscriptProjection(generation: number, history: unknown[], tail: unknown[]): void {
  const key = paneKeyOf(generation);
  lru.delete(key);
  lru.set(key, { generation, history, tail, at: Date.now() });
  while (lru.size > TRANSCRIPT_CACHE_LIMIT) {
    const oldest = lru.keys().next();
    if (oldest.done) break;
    lru.delete(oldest.value);
  }
  retainedPanes.delete(key);
  retainedPanes.set(key, Date.now());
  while (retainedPanes.size > RETAINED_PANE_LIMIT) {
    const oldest = retainedPanes.keys().next();
    if (oldest.done) break;
    const k = oldest.value;
    retainedPanes.delete(k);
    lru.delete(k);
  }
}

export function getCachedProjection(generation: number): CacheRow | null {
  const row = lru.get(paneKeyOf(generation)) || null;
  if (row) {
    lru.delete(paneKeyOf(generation));
    lru.set(paneKeyOf(generation), { ...row, at: Date.now() });
  }
  return row;
}

export function cacheSize(): number {
  return lru.size;
}

export function clearTranscriptCache(): void {
  lru.clear();
  retainedPanes.clear();
}
