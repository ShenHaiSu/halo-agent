import type { ContractEventFrame } from '../contract/agent';
import type { EventBatch, FrameTransport } from './types';
import { MAX_BATCH, SEQ_STORAGE_KEY } from './types';

export type FrameHandler = (frame: ContractEventFrame) => void;
export type GapHandler = (info: { droppedEstimate: number }) => void;

/**
 * Frame-driven poll loop for spec and storybook scenarios. It pulls one batch per
 * animation frame so bursts arrive without stacking callbacks, while slow
 * consumers naturally widen the batch instead of queuing work. The persisted
 * cursor survives reloads; a reported gap pauses polling until the owner
 * finishes snapshot-backed resync and resumes from the new cursor.
 * Production traffic uses EventBridge (timer based); this class stays test-only.
 */
export class FramePump {
  private seq: number;
  private rafId: number | null = null;
  private running = false;
  private readonly frameHandlers = new Set<FrameHandler>();
  private readonly gapHandlers = new Set<GapHandler>();

  constructor(
    private readonly transport: FrameTransport,
    private readonly storageKey: string = SEQ_STORAGE_KEY
  ) {
    this.seq = loadCursor(storageKey);
  }

  get lastSeq(): number {
    return this.seq;
  }

  onFrame(handler: FrameHandler): () => void {
    this.frameHandlers.add(handler);
    return () => this.frameHandlers.delete(handler);
  }

  onGap(handler: GapHandler): () => void {
    this.gapHandlers.add(handler);
    return () => this.gapHandlers.delete(handler);
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.schedule();
  }

  stop(): void {
    this.running = false;
    if (this.rafId !== null) {
      cancelFrame(this.rafId);
      this.rafId = null;
    }
  }

  /** Advance the cursor after the owner rebuilt state elsewhere. */
  resumeFrom(nextSeq: number): void {
    this.seq = Math.max(0, Math.floor(nextSeq));
    this.persist();
    if (this.running) this.schedule();
  }

  /** Single pull step, also used directly by tests. */
  async pullOnce(): Promise<EventBatch | null> {
    let batch: EventBatch;
    try {
      batch = await this.transport.pollBatch(this.seq, MAX_BATCH);
    } catch {
      return null;
    }
    if (batch.hasGap) {
      this.pauseForGap(batch.droppedEstimate);
      return batch;
    }
    const fresh = (batch.frames || []).filter(
      (frame) => frame && frame.t === 'evt' && typeof frame.seq === 'number' && frame.seq > this.seq,
    );
    if (fresh.length > 0) {
      for (const frame of fresh) {
        for (const handler of [...this.frameHandlers]) {
          try {
            handler(frame);
          } catch {
            /* a bad subscriber must not break the loop */
          }
        }
      }
      const tail = fresh[fresh.length - 1];
      const highest = tail && typeof tail.seq === 'number' ? tail.seq : this.seq;
      this.seq = Math.max(batch.nextSeq, highest);
      this.persist();
    }
    return batch;
  }

  private schedule(): void {
    if (!this.running) return;
    this.rafId = requestFrame(() => void this.tick());
  }

  private async tick(): Promise<void> {
    this.rafId = null;
    if (!this.running) return;
    await this.pullOnce();
    this.schedule();
  }

  private pauseForGap(droppedEstimate: number): void {
    this.stop();
    for (const handler of [...this.gapHandlers]) {
      try {
        handler({ droppedEstimate });
      } catch {
        /* ignore subscriber errors */
      }
    }
  }

  private persist(): void {
    try {
      localStorage.setItem(this.storageKey, String(this.seq));
    } catch {
      /* storage unavailable: cursor stays in memory */
    }
  }
}

function loadCursor(key: string): number {
  try {
    const raw = localStorage.getItem(key);
    const value = raw === null ? Number.NaN : Number(raw);
    return Number.isFinite(value) && value >= 0 ? Math.floor(value) : 0;
  } catch {
    return 0;
  }
}

function requestFrame(callback: () => void): number {
  try {
    const raf = (
      globalThis as unknown as {
        requestAnimationFrame?: (cb: () => void) => number;
      }
    ).requestAnimationFrame;
    if (typeof raf === 'function') return raf.call(globalThis, callback);
  } catch {
    /* fall through to the timer fallback */
  }
  return Number(setTimeout(callback, 16));
}

function cancelFrame(id: number): void {
  try {
    const cancel = (
      globalThis as unknown as {
        cancelAnimationFrame?: (id: number) => void;
      }
    ).cancelAnimationFrame;
    if (typeof cancel === 'function') {
      cancel.call(globalThis, id);
      return;
    }
  } catch {
    /* fall through */
  }
  clearTimeout(id);
}
