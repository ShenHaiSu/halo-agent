/** bigChange P2-1: rAF-batched leaf writes. Map<key,latest> coalescing; terminal events flushNow. */
export type FrameBatcherKey = string;

interface BatchEntry {
  key: FrameBatcherKey;
  apply: () => void;
}

let pending = new Map<FrameBatcherKey, BatchEntry>();
let rafId = 0;
let fallbackTimer: number | null = null;

function hasRaf(): boolean {
  return typeof requestAnimationFrame === 'function';
}

function scheduleFlush(): void {
  if (rafId) return;
  if (hasRaf()) {
    rafId = requestAnimationFrame(() => {
      rafId = 0;
      flushNow();
    });
    return;
  }
  if (fallbackTimer !== null) return;
  fallbackTimer = window.setTimeout(() => {
    fallbackTimer = null;
    flushNow();
  }, 16);
}

/** Enqueue latest value per key; same-key re-enqueue overwrites (coalesced). Text deltas must be pre-appended by caller via mergeEnvelopes. */
export function enqueueFrame(key: FrameBatcherKey, apply: () => void): void {
  pending.set(key, { key, apply });
  scheduleFlush();
}

/** Append-merge helper for streaming text envelopes: appends chunk to current rather than replacing. */
export function mergeTextAppend(current: string, chunk: string): string {
  return (current || '') + (chunk || '');
}

/** Flush all pending leaf writes now (terminal events: message_end/tool_end/non-stream). */
export function flushNow(): void {
  if (pending.size === 0) return;
  const batch = pending;
  pending = new Map();
  if (rafId && hasRaf()) {
    try {
      cancelAnimationFrame(rafId);
    } catch {
      /* ignore */
    }
    rafId = 0;
  }
  if (fallbackTimer !== null) {
    try {
      window.clearTimeout(fallbackTimer);
    } catch {
      /* ignore */
    }
    fallbackTimer = null;
  }
  for (const entry of batch.values()) {
    try {
      entry.apply();
    } catch (e) {
      console.warn('[frameBatcher] apply failed:', entry.key, e);
    }
  }
}

/** Event key helpers: message:{sid}/{mid} and tool:{callId}. */
export function messageKey(sid: string, mid: string): FrameBatcherKey {
  return `message:${sid}/${mid}`;
}
export function toolKey(callId: string): FrameBatcherKey {
  return `tool:${callId}`;
}

/** Test hook: pending count. */
export function pendingCount(): number {
  return pending.size;
}
