import type { ContractEventFrame } from '../contract/agent';
import type { FrameTransport } from '../transport/types';
import { MAX_BATCH, SEQ_STORAGE_KEY } from '../transport/types';

export type EventHandler = (frame: ContractEventFrame) => void;
export type ResyncHandler = (info: { hasGap: boolean; droppedEstimate: number }) => void;
export type PumpStatsHandler = (stats: PumpStats) => void;
export type PumpNoticeHandler = (notice: { kind: 'degraded'; consecutiveFailures: number }) => void;

export interface PumpStats {
  polls: number;
  batches: number;
  gaps: number;
  resyncs: number;
}

/**
 * Single owner of the read cursor and batch fan-out. It polls the transport
 * on an adaptive timer (busy pulls fast, idle backs off), persists the
 * cursor after every batch, and routes frames to subscribers. A reported
 * gap rebuilds from a snapshot-backed resync before polling continues.
 *
 * Production main pump: 30ms busy / 200ms idle / 2s backoff cap.
 * The rAF frame pump stays a test-only pump; business code subscribes here.
 */
export class EventBridge {
  private seq = 0;
  private readonly handlers = new Map<string, Set<EventHandler>>();
  private readonly resyncHandlers = new Set<ResyncHandler>();
  private readonly streamHandlers = new Map<string, Set<EventHandler>>();
  private streamQueue: ContractEventFrame[] = [];
  private streamFlushScheduled = false;
  private noticeSent = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running = false;
  private failures = 0;
  private readonly idleMs: number;
  private readonly busyMs: number;
  private readonly backoffCapMs: number;
  private readonly storageKey: string;
  private polls = 0;
  private batches = 0;
  private gaps = 0;
  private resyncs = 0;
  private readonly statsSink: PumpStatsHandler | null;
  private readonly noticeSink: PumpNoticeHandler | null;

  constructor(
    private readonly transport: FrameTransport,
    options: {
      storageKey?: string;
      idleMs?: number;
      busyMs?: number;
      backoffCapMs?: number;
      onStats?: PumpStatsHandler;
      onNotice?: PumpNoticeHandler;
    } = {},
  ) {
    this.idleMs = options.idleMs ?? 200;
    this.busyMs = options.busyMs ?? 30;
    this.backoffCapMs = options.backoffCapMs ?? 2000;
    this.storageKey = options.storageKey ?? SEQ_STORAGE_KEY;
    this.statsSink = options.onStats ?? null;
    this.noticeSink = options.onNotice ?? null;
    this.seq = loadSeq(this.storageKey);
  }

  get lastSeq(): number {
    return this.seq;
  }

  get stats(): PumpStats {
    return { polls: this.polls, batches: this.batches, gaps: this.gaps, resyncs: this.resyncs };
  }

  /** Beat contract asserted by specs: 30ms busy / 200ms idle / 2s backoff cap. */
  get beat(): { busyMs: number; idleMs: number; backoffCapMs: number } {
    return { busyMs: this.busyMs, idleMs: this.idleMs, backoffCapMs: this.backoffCapMs };
  }

  on(kind: string, handler: EventHandler): () => void {
    let bucket = this.handlers.get(kind);
    if (!bucket) {
      bucket = new Set();
      this.handlers.set(kind, bucket);
    }
    bucket.add(handler);
    return () => bucket?.delete(handler);
  }

  /** Stream lane: delta frames flush on an animation frame so bursts stay smooth. */
  onStream(kind: string, handler: EventHandler): () => void {
    let bucket = this.streamHandlers.get(kind);
    if (!bucket) {
      bucket = new Set();
      this.streamHandlers.set(kind, bucket);
    }
    bucket.add(handler);
    return () => bucket?.delete(handler);
  }

  onResync(handler: ResyncHandler): () => void {
    this.resyncHandlers.add(handler);
    return () => this.resyncHandlers.delete(handler);
  }

  /** Catch the cursor up after a reload without emitting old frames twice. */
  async refreshBaseline(): Promise<void> {
    const snapshot = await this.transport.invoke('state.snapshot', {});
    const latest = snapshot.result?.['latestSequence'];
    if (typeof latest === 'number' && latest > this.seq) {
      this.seq = latest;
      this.persist();
    }
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.failures = 0;
    this.schedule(this.idleMs);
  }

  stop(): void {
    this.running = false;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }

  /** Single poll step, also used directly by tests. */
  async pollOnce(): Promise<void> {
    let batch;
    this.polls += 1;
    try {
      batch = await this.transport.pollBatch(this.seq, MAX_BATCH);
      this.failures = 0;
      this.noticeSent = false;
    } catch {
      this.failures += 1;
      this.emitDegraded();
      this.emitStats();
      return;
    }
    if (batch.hasGap) {
      this.gaps += 1;
      await this.resync(batch.hasGap, batch.droppedEstimate);
      this.emitStats();
      return;
    }
    const fresh = (batch.frames || []).filter((frame) => {
      if (!frame || frame.t !== 'evt') return false;
      // Cursor dedup: never re-emit frames at or below the cursor.
      return typeof frame.seq === 'number' && frame.seq > this.seq;
    });
    if (fresh.length > 0) {
      this.dispatch(fresh);
      const tail = fresh[fresh.length - 1];
      const highest = tail && typeof tail.seq === 'number' ? tail.seq : this.seq;
      this.seq = Math.max(this.seq, Math.max(batch.nextSeq, highest));
      this.persist();
      this.batches += 1;
    } else if (typeof batch.nextSeq === 'number' && batch.nextSeq > this.seq) {
      this.seq = batch.nextSeq;
      this.persist();
    }
    this.emitStats();
  }

  private schedule(delayMs: number): void {
    if (!this.running) return;
    this.timer = setTimeout(() => void this.tick(), delayMs);
  }

  private async tick(): Promise<void> {
    if (!this.running) return;
    const before = this.seq;
    await this.pollOnce();
    const progressed = this.seq !== before;
    const backoff = Math.min(this.idleMs * 2 ** Math.min(this.failures, 4), this.backoffCapMs);
    this.schedule(progressed ? this.busyMs : backoff);
  }

  private dispatch(frames: ContractEventFrame[]): void {
    for (const frame of frames) {
      this.emitKind(this.handlers, frame.k, frame);
      this.emitKind(this.handlers, '*', frame);
      if (this.streamHandlers.has(frame.k) || this.streamHandlers.has('*')) {
        this.streamQueue.push(frame);
      }
    }
    this.scheduleStreamFlush();
  }

  private scheduleStreamFlush(): void {
    if (this.streamFlushScheduled || this.streamQueue.length === 0) return;
    this.streamFlushScheduled = true;
    const flush = (): void => {
      this.streamFlushScheduled = false;
      const batch = this.streamQueue;
      this.streamQueue = [];
      for (const frame of batch) {
        this.emitKind(this.streamHandlers, frame.k, frame);
        this.emitKind(this.streamHandlers, '*', frame);
      }
    };
    try {
      const raf = (globalThis as unknown as { requestAnimationFrame?: (cb: () => void) => number }).requestAnimationFrame;
      if (typeof raf === 'function') {
        raf.call(globalThis, flush);
        return;
      }
    } catch {
      /* fall through to the timer fallback */
    }
    setTimeout(flush, 16);
  }

  private emitKind(subscribers: Map<string, Set<EventHandler>>, kind: string, frame: ContractEventFrame): void {
    const bucket = subscribers.get(kind);
    if (!bucket) return;
    for (const handler of [...bucket]) {
      try {
        handler(frame);
      } catch {
        /* a bad subscriber must not break the pump */
      }
    }
  }

  private async resync(hasGap: boolean, droppedEstimate: number): Promise<void> {
    let latest = Number.NaN;
    try {
      const snapshot = await this.transport.invoke('state.snapshot', {});
      const value = snapshot.result?.['latestSequence'];
      if (typeof value === 'number' && Number.isFinite(value) && value >= 0) {
        latest = Math.floor(value);
      }
    } catch {
      /* snapshot failure: keep the cursor and retry on the next tick */
      return;
    }
    if (!Number.isFinite(latest)) return;
    this.seq = latest;
    this.persist();
    this.resyncs += 1;
    for (const handler of [...this.resyncHandlers]) {
      try {
        handler({ hasGap, droppedEstimate });
      } catch {
        /* ignore subscriber errors */
      }
    }
  }

  private emitDegraded(): void {
    if (this.noticeSent || this.failures < 3 || !this.noticeSink) return;
    this.noticeSent = true;
    try {
      this.noticeSink({ kind: 'degraded', consecutiveFailures: this.failures });
    } catch {
      /* notices must never break the pump */
    }
  }

  private emitStats(): void {
    if (!this.statsSink) return;
    try {
      this.statsSink(this.stats);
    } catch {
      /* stats must never break the pump */
    }
  }

  private persist(): void {
    saveSeq(this.storageKey, this.seq);
  }
}

function loadSeq(key: string): number {
  try {
    const raw = localStorage.getItem(key);
    const value = raw === null ? Number.NaN : Number(raw);
    return Number.isFinite(value) && value >= 0 ? Math.floor(value) : 0;
  } catch {
    return 0;
  }
}

function saveSeq(key: string, seq: number): void {
  try {
    localStorage.setItem(key, String(seq));
  } catch {
    /* storage unavailable: cursor stays in memory */
  }
}
