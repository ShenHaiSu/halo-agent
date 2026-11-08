import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { ContractEventFrame } from '@/contract/agent';
import type { EventBatch, FrameTransport, InvokeResult } from '@/transport/types';
import { useFrameSessionStore } from './frameSessionStore';

interface StubRow {
  index: number;
  role: string;
  text: string;
  turnId?: string;
}

/** Frame backing with durable rows plus an event journal for turn settle. */
class ConvergeStub implements FrameTransport {
  readonly titles = new Map<string, string>([['session-a', 'Alpha']]);
  rows: StubRow[] = [
    { index: 1, role: 'user', text: 'first', turnId: 'turn-0' },
    { index: 2, role: 'assistant', text: 'second', turnId: 'turn-0' },
    { index: 3, role: 'user', text: 'third', turnId: 'turn-1' },
  ];
  readonly journal: ContractEventFrame[] = [];
  readonly seenPrompts: Array<Record<string, unknown>> = [];
  emitTurnDone = true;
  promptFails = false;
  private seq = 0;
  private turns = 0;

  async invoke(method: string, params: Record<string, unknown> = {}): Promise<InvokeResult> {
    switch (method) {
      case 'state.snapshot':
        return { ok: true, result: { activeSessionId: 'session-a', latestSequence: this.seq } };
      case 'session.list':
        return {
          ok: true,
          result: {
            sessions: [...this.titles.entries()].map(([sessionId, title]) => ({ sessionId, title })),
          },
        };
      case 'session.get': {
        const id = String(params['sessionId'] ?? '');
        const title = this.titles.get(id);
        if (!title) return { ok: false, error: { code: 'NOT_FOUND', msg: 'Session is unknown.' } };
        return { ok: true, result: { sessionId: id, title } };
      }
      case 'session.history': {
        const since = Number.parseInt(String(params['cursor'] ?? ''), 10);
        const origin = Number.isFinite(since) ? since : 0;
        const window = this.rows.filter((row) => row.index > origin).slice(0, 2);
        const last = window[window.length - 1];
        const max = this.rows.reduce((top, row) => Math.max(top, row.index), 0);
        return {
          ok: true,
          result: {
            sessionId: params['sessionId'],
            items: window.map((row) => ({ ...row })),
            nextCursor: last ? String(last.index) : String(origin),
            hasMore: last ? last.index < max : false,
            total: this.rows.length,
          },
        };
      }
      case 'agent.prompt': {
        if (this.promptFails) {
          return { ok: false, error: { code: 'NOT_FOUND', msg: 'Session is unknown.' } };
        }
        this.seenPrompts.push({ ...params });
        this.turns += 1;
        const turnId = `turn-stub-${this.turns}`;
        const next = this.rows.reduce((top, row) => Math.max(top, row.index), 0) + 1;
        this.rows.push({ index: next, role: 'user', text: String(params['text'] ?? ''), turnId });
        if (this.emitTurnDone) {
          this.seq += 1;
          this.journal.push({
            t: 'evt',
            seq: this.seq,
            k: 'turn.done',
            p: { turnId, sessionId: params['sessionId'] },
          });
        }
        return { ok: true, result: { turnId } };
      }
      default:
        return { ok: false, error: { code: 'NOT_FOUND', msg: `unexpected ${method}` } };
    }
  }

  async pollBatch(sinceSeq: number): Promise<EventBatch> {
    const frames = this.journal.filter((frame) => frame.seq > sinceSeq);
    const last = frames[frames.length - 1];
    return {
      frames,
      nextSeq: last ? last.seq : Math.max(sinceSeq, this.seq),
      hasGap: false,
      droppedEstimate: 0,
    };
  }
}

describe('history convergent read', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('opens with detail first and reports the backend total', async () => {
    const store = useFrameSessionStore();
    store.bind(new ConvergeStub());
    expect(await store.openSession('session-a')).toBe(true);
    expect(store.detailTitle).toBe('Alpha');
    expect(store.history.map((entry) => entry.content)).toEqual(['first', 'second']);
    expect(store.history[0]?.id).toBe('msg_1_turn-0');
    expect(store.total).toBe(3);
    expect(store.hasMore).toBe(true);
  });

  it('reads unknown sessions as empty pages without errors', async () => {
    const store = useFrameSessionStore();
    store.bind(new ConvergeStub());
    expect(await store.openSession('missing-session')).toBe(true);
    expect(store.history).toEqual([]);
    expect(store.total).toBe(0);
    expect(store.lastError).toBe('');
  });

  it('skips unreadable rows while keeping the backend total', async () => {
    const store = useFrameSessionStore();
    const stub = new ConvergeStub();
    stub.rows = [
      { index: 1, role: 'user', text: 'kept' },
      { index: 2, role: 'assistant', text: 'kept too' },
    ];
    store.bind({
      invoke: async (method: string) => {
        if (method === 'session.get') {
          return { ok: true, result: { sessionId: 'session-a', title: 'Alpha' } };
        }
        return {
          ok: true,
          result: {
            items: [...stub.rows, null, { role: 'assistant', text: 'no index' }],
            nextCursor: '2',
            hasMore: false,
            total: 2,
          },
        };
      },
      pollBatch: async (sinceSeq: number): Promise<EventBatch> => ({
        frames: [],
        nextSeq: sinceSeq,
        hasGap: false,
        droppedEstimate: 0,
      }),
    } satisfies FrameTransport);
    expect(await store.openSession('session-a')).toBe(true);
    expect(store.history.length).toBe(2);
    expect(store.skippedRows).toBe(2);
    expect(store.total).toBe(2);
  });

  it('catches up incrementally past the current cursor', async () => {
    const store = useFrameSessionStore();
    const stub = new ConvergeStub();
    store.bind(stub);
    await store.openSession('session-a');
    expect(store.history.length).toBe(2);
    stub.rows.push({ index: 4, role: 'assistant', text: 'fourth', turnId: 'turn-2' });
    expect(await store.refreshActive()).toBe(true);
    expect(store.history.map((entry) => entry.content)).toEqual(['first', 'second', 'third', 'fourth']);
    expect(store.total).toBe(4);
  });

  it('echoes the prompt at once and collapses once the twin lands', async () => {
    const store = useFrameSessionStore();
    const stub = new ConvergeStub();
    store.bind(stub);
    await store.openSession('session-a');
    store.showPendingPrompt('brand new question', 'req-echo-1');
    expect(store.hasPending).toBe(true);
    expect(store.visibleHistory.length).toBe(store.history.length + 1);
    expect(store.visibleHistory[store.visibleHistory.length - 1]?.content).toBe('brand new question');
    stub.rows.push({ index: 4, role: 'user', text: 'brand new question', turnId: 'turn-9' });
    await store.refreshActive();
    expect(store.visibleHistory.length).toBe(store.history.length);
    expect(store.hasPending).toBe(false);
  });

  it('sends with a request key and settles on the matching turn event', async () => {
    const store = useFrameSessionStore();
    const stub = new ConvergeStub();
    store.bind(stub);
    await store.openSession('session-a');
    const before = store.history.length;
    expect(await store.promptAndConverge('hello frame', { requestId: 'req-typed-1' })).toBe(true);
    expect(stub.seenPrompts.length).toBe(1);
    expect(stub.seenPrompts[0]?.['requestId']).toBe('req-typed-1');
    expect(stub.seenPrompts[0]?.['text']).toBe('hello frame');
    expect(store.history.length).toBeGreaterThan(before);
    expect(store.history[store.history.length - 1]?.content).toBe('hello frame');
    expect(store.hasPending).toBe(false);
    expect(store.total).toBe(stub.rows.length);
  });

  it('clears the echo when the prompt is rejected', async () => {
    const store = useFrameSessionStore();
    const stub = new ConvergeStub();
    stub.promptFails = true;
    store.bind(stub);
    await store.openSession('session-a');
    expect(await store.promptAndConverge('hello frame')).toBe(false);
    expect(store.hasPending).toBe(false);
    expect(store.visibleHistory.length).toBe(store.history.length);
    expect(store.lastError).not.toBe('');
  });

  it('reports a timeout when the turn event never arrives', async () => {
    const store = useFrameSessionStore();
    const stub = new ConvergeStub();
    stub.emitTurnDone = false;
    store.bind(stub);
    await store.openSession('session-a');
    expect(await store.promptAndConverge('hello frame', { timeoutMs: 10 })).toBe(false);
    expect(store.lastError).toBe('Turn confirmation timed out.');
    expect(store.hasPending).toBe(false);
  });
});
