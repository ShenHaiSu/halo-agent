import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { EventBatch, FrameTransport, InvokeResult } from '@/transport/types';
import { MockTransport } from '@/transport/mockTransport';
import { useFrameSessionStore } from './frameSessionStore';

/** Scripted backing with two history pages for the visible-window store. */
function pagedStub(): FrameTransport {
  return {
    invoke: async (method: string, params: Record<string, unknown> = {}): Promise<InvokeResult> => {
      switch (method) {
        case 'state.snapshot':
          return { ok: true, result: { activeSessionId: 'session-a', latestSequence: 42 } };
        case 'session.list':
          return {
            ok: true,
            result: { sessions: [{ sessionId: 'session-a', title: 'Alpha' }] },
          };
        case 'session.get':
          return {
            ok: true,
            result: { sessionId: params['sessionId'], title: 'Alpha', messageCount: 3 },
          };
        case 'session.history': {
          const cursor = params['cursor'];
          if (cursor === 'page-two') {
            return {
              ok: true,
              result: {
                sessionId: params['sessionId'],
                items: [{ index: 0, role: 'user', text: 'first', turnId: 'turn-0' }],
                nextCursor: null,
                hasMore: false,
              },
            };
          }
          return {
            ok: true,
            result: {
              sessionId: params['sessionId'],
              items: [
                { index: 2, role: 'assistant', text: 'third', turnId: 'turn-2' },
                { index: 1, role: 'user', text: 'second', turnId: 'turn-1' },
              ],
              nextCursor: 'page-two',
              hasMore: true,
            },
          };
        }
        default:
          return { ok: false, error: { code: 'NOT_FOUND', msg: `unexpected ${method}` } };
      }
    },
    pollBatch: async (sinceSeq: number): Promise<EventBatch> => ({
      frames: [],
      nextSeq: sinceSeq,
      hasGap: false,
      droppedEstimate: 0,
    }),
  };
}

describe('frame session store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('stays idle until a transport is bound', async () => {
    const store = useFrameSessionStore();
    expect(store.isBound).toBe(false);
    expect(await store.refreshFromSnapshot()).toBe(false);
    expect(await store.loadList()).toBe(false);
    expect(store.lastError).not.toBe('');
  });

  it('rebuilds the visible window from snapshot, list and first page', async () => {
    const store = useFrameSessionStore();
    store.bind(pagedStub());
    expect(await store.refreshFromSnapshot()).toBe(true);
    expect(store.activeSessionId).toBe('session-a');
    expect(store.latestSequence).toBe(42);
    expect(await store.loadList()).toBe(true);
    expect(store.sessions).toEqual([{ sessionId: 'session-a', title: 'Alpha' }]);
    expect(await store.openSession('session-a')).toBe(true);
    expect(store.detailTitle).toBe('Alpha');
    expect(store.messageCount).toBe(2);
    expect(store.hasMore).toBe(true);
  });

  it('appends older pages without touching the loaded prefix', async () => {
    const store = useFrameSessionStore();
    store.bind(pagedStub());
    await store.openSession('session-a');
    const prefix = store.history.map((entry) => entry.content);
    expect(await store.loadMore()).toBe(true);
    expect(store.history.map((entry) => entry.content)).toEqual([...prefix, 'first']);
    expect(store.hasMore).toBe(false);
    expect(await store.loadMore()).toBe(false);
  });

  it('discards everything for a clean rebuild', async () => {
    const store = useFrameSessionStore();
    store.bind(new MockTransport());
    await store.refreshFromSnapshot();
    store.reset();
    expect(store.activeSessionId).toBe('');
    expect(store.sessions).toEqual([]);
    expect(store.history).toEqual([]);
  });
});
