import type { ContractEventFrame } from '../contract/agent';
import type { FrameTransport } from '../transport/types';
import { MAX_BATCH, SEQ_STORAGE_KEY } from '../transport/types';

export type EventHandler = (frame: ContractEventFrame) => void;
export type ResyncHandler = (info: { hasGap: boolean; droppedEstimate: number }) => void;

/**
 * Single owner of the read cursor and batch fan-out. It polls the transport
 * on an adaptive timer (busy pulls fast, idle backs off), persists the
 * cursor after every batch, and routes frames to subscribers. A reported
 * gap triggers a snapshot-backed resync before polling continues.
 */
export class EventBridge {
  private seq = 0;
  private readonly handlers = new Map<string, Set<EventHandler>>();
  private readonly resyncHandlers = new Set<ResyncHandler>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running = false;
  private failures = 0;
  private readonly idleMs: number;
  private readonly busyMs: number;
  private readonly backoffCapMs: number;

  constructor(
    private readonly transport: FrameTransport,
    options: { storageKey?: string; idleMs?: number; busyMs?: number; backoffCapMs?: number } = {}
  ) {
    this.idleMs = options.idleMs ?? 200;
    this.busyMs = options.busyMs ?? 30;
    this.backoffCapMs = options.backoffCapMs ?? 2000;
    this.seq = loadSeq(options.storageKey ?? SEQ_STORAGE_KEY);
  }

  get lastSeq(): number {
    return this.seq;
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
    try {
      batch = await this.transport.pollBatch(this.seq, MAX_BATCH);
      this.failures = 0;
    } catch {
      this.failures += 1;
      return;
    }
    if (batch.hasGap) {
      await this.resync(batch.hasGap, batch.droppedEstimate);
      return;
    }
    if (batch.frames.length > 0) {
      this.dispatch(batch.frames);
      this.seq = batch.nextSeq;
      this.persist();
    }
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
      this.emitKind(frame.k, frame);
      this.emitKind('*', frame);
    }
  }

  private emitKind(kind: string, frame: ContractEventFrame): void {
    const bucket = this.handlers.get(kind);
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
    try {
      await this.transport.invoke('state.snapshot', {});
    } catch {
      /* snapshot failure: keep the cursor and retry on the next tick */
      return;
    }
    this.seq = 0;
    this.persist();
    for (const handler of [...this.resyncHandlers]) {
      try {
        handler({ hasGap, droppedEstimate });
      } catch {
        /* ignore subscriber errors */
      }
    }
  }

  private persist(): void {
    saveSeq(SEQ_STORAGE_KEY, this.seq);
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
