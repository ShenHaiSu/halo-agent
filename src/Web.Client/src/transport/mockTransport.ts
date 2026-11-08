import type { ContractEventFrame, ContractMethod } from '../contract/agent';
import type { EventBatch, FrameTransport, InvokeResult } from './types';
import { MAX_BATCH } from './types';

interface SessionRecord {
  sessionId: string;
  title: string;
}

/**
 * In-memory frame transport for development and tests. It needs no host:
 * sessions, turns and the event journal live inside this object, so the
 * main flow (ping, new session, prompt, batched poll, snapshot) runs under
 * the dev server. Snapshots are plain data and can be reused across scenarios.
 */
export class MockTransport implements FrameTransport {
  private seq = 0;
  private readonly journal: ContractEventFrame[] = [];
  private readonly sessions = new Map<string, SessionRecord>();
  private activeSessionId = '';
  private turnCounter = 0;
  private droppedTotal = 0;
  readonly capacity = 4096;

  reset(): void {
    this.seq = 0;
    this.journal.length = 0;
    this.sessions.clear();
    this.activeSessionId = '';
    this.turnCounter = 0;
    this.droppedTotal = 0;
  }

  /** Seed a named scenario; `main-flow` creates one session for instant driving. */
  seedScenario(name: string): { sessionId: string } {
    this.reset();
    const sessionId = `mock-session-${name}-1`;
    this.sessions.set(sessionId, { sessionId, title: `Mock ${name}` });
    this.activeSessionId = sessionId;
    this.append('session.updated', { sessionId, reason: 'seeded' });
    return { sessionId };
  }

  /** Reusable snapshot payload matching the skeleton server shape. */
  snapshotPayload(): Record<string, unknown> {
    return {
      activeSessionId: this.activeSessionId,
      mode: 'normal',
      personaId: 'default',
      maxTurns: 10,
      latestSequence: this.seq,
    };
  }

  private append(kind: ContractEventFrame['k'], params: Record<string, unknown> = {}): number {
    this.seq += 1;
    this.journal.push({ t: 'evt', seq: this.seq, k: kind, p: params });
    while (this.journal.length > this.capacity) {
      this.journal.shift();
      this.droppedTotal += 1;
    }
    return this.seq;
  }

  async invoke(method: ContractMethod, params: Record<string, unknown> = {}): Promise<InvokeResult> {
    switch (method) {
      case 'system.ping':
        return { ok: true, result: { pong: true } };
      case 'system.info':
        return {
          ok: true,
          result: {
            version: '0.1.0',
            queueBound: 64,
            journalCapacity: this.capacity,
            routing: { enabledBatches: [] },
            bridgeVersion: 2,
            contractVersion: '0.1.1',
          },
        };
      case 'session.new': {
        const title = typeof params['title'] === 'string' && params['title'].trim() !== '' ? (params['title'] as string) : 'New session';
        const sessionId = `mock-session-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6)}`;
        this.sessions.set(sessionId, { sessionId, title });
        this.activeSessionId = sessionId;
        this.append('session.updated', { sessionId, reason: 'created' });
        return { ok: true, result: { sessionId } };
      }
      case 'session.list': {
        const sessions = [...this.sessions.values()].map((s) => ({ sessionId: s.sessionId, title: s.title }));
        return { ok: true, result: { sessions } };
      }
      case 'session.get': {
        const id = params['sessionId'] as string | undefined;
        const found = id ? this.sessions.get(id) : undefined;
        if (!found) return { ok: false, error: { code: 'NOT_FOUND', msg: 'Session is unknown.' } };
        return { ok: true, result: { sessionId: found.sessionId, title: found.title } };
      }
      case 'session.history':
        return { ok: true, result: { messages: [], nextCursor: null } };
      case 'agent.prompt': {
        const sessionId = params['sessionId'] as string | undefined;
        const text = params['text'] as string | undefined;
        if (!sessionId || !text) {
          return { ok: false, error: { code: 'INVALID_PARAMS', msg: 'sessionId and text are required.' } };
        }
        if (!this.sessions.has(sessionId)) {
          return { ok: false, error: { code: 'NOT_FOUND', msg: 'Session is unknown.' } };
        }
        this.turnCounter += 1;
        const turnId = `mock-turn-${this.turnCounter}`;
        this.append('turn.started', { turnId, sessionId });
        this.append('text.delta', { turnId, delta: text });
        this.append('text.done', { turnId, length: text.length });
        this.append('turn.done', { turnId, sessionId });
        return { ok: true, result: { turnId } };
      }
      case 'agent.cancel': {
        const turnId = params['turnId'] as string | undefined;
        if (!turnId) return { ok: false, error: { code: 'INVALID_PARAMS', msg: 'turnId is required for cancel.' } };
        this.append('turn.cancelled', { turnId, signaled: true });
        return { ok: true, result: { turnId, signaled: true } };
      }
      case 'state.snapshot':
        return { ok: true, result: this.snapshotPayload() };
      case 'usage.query':
        return { ok: true, result: { input: 0, output: 0, waters: this.snapshotPayload().latestSequence } };
      default:
        return { ok: true, result: { stub: true, method } };
    }
  }

  async pollBatch(sinceSeq: number, max: number): Promise<EventBatch> {
    const take = Math.min(Math.max(max, 1), MAX_BATCH);
    let hasGap = false;
    let droppedEstimate = 0;
    if (this.journal.length > 0) {
      const oldest = this.journal[0]?.seq ?? 1;
      if (sinceSeq < oldest - 1) {
        hasGap = true;
        droppedEstimate = oldest - 1 - sinceSeq + this.droppedTotal;
      }
    } else if (sinceSeq < this.seq) {
      hasGap = true;
      droppedEstimate = this.seq - sinceSeq;
    }
    const frames = this.journal.filter((f) => f.seq > sinceSeq).slice(0, take);
    const nextSeq = frames.length > 0 ? frames[frames.length - 1]!.seq : Math.max(sinceSeq, this.seq);
    return { frames, nextSeq, hasGap, droppedEstimate };
  }
}
