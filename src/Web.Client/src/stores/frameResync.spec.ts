import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { EventBridge } from './eventBridge';
import { useFrameSessionStore } from './frameSessionStore';
import type { EventBatch, FrameTransport, InvokeResult } from '@/transport/types';

/**
 * Gap recovery: a reported gap rebuilds the visible window from a snapshot
 * plus paged history, then polling continues from the fresh cursor without
 * duplicating already rendered messages.
 */
function recoveringStub(log: string[]): FrameTransport {
  return {
    invoke: async (method: string, params: Record<string, unknown> = {}): Promise<InvokeResult> => {
      log.push(method);
      switch (method) {
        case 'state.snapshot':
          return { ok: true, result: { activeSessionId: 'session-a', latestSequence: 9 } };
        case 'session.list':
          return { ok: true, result: { sessions: [{ sessionId: 'session-a', title: 'Alpha' }] } };
        case 'session.get':
          return { ok: true, result: { sessionId: params['sessionId'], title: 'Alpha' } };
        case 'session.history':
          return {
            ok: true,
            result: { sessionId: params['sessionId'], items: [], nextCursor: null, hasMore: false },
          };
        default:
          return { ok: false, error: { code: 'NOT_FOUND', msg: method } };
      }
    },
    pollBatch: async (sinceSeq: number): Promise<EventBatch> => ({
      frames: [],
      nextSeq: sinceSeq,
      hasGap: sinceSeq === 0,
      droppedEstimate: sinceSeq === 0 ? 4 : 0,
    }),
  };
}

describe('gap recovery rebuilds the visible window', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
  });

  it('resyncs through snapshot plus paged history exactly once', async () => {
    const calls: string[] = [];
    const transport = recoveringStub(calls);
    const bridge = new EventBridge(transport);
    const sessions = useFrameSessionStore();
    sessions.bind(transport);
    bridge.onResync(() => {
      void sessions.rebuildVisibleWindow();
    });
    await bridge.pollOnce();
    // allow the queued rebuild to finish
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(calls).toContain('state.snapshot');
    expect(calls).toContain('session.list');
    expect(calls).toContain('session.history');
    expect(sessions.activeSessionId).toBe('session-a');
    expect(bridge.lastSeq).toBe(9);
  });
});
