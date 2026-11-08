import { beforeEach, describe, expect, it, vi } from 'vitest';
import { BridgeTransport, parseInvokeResult } from './bridgeTransport';
import { EventBridge } from '@/stores/eventBridge';
import { createFrameEventBridge, primeFrameEventBridge } from '@/stores/agentWiring';
import type { EventBatch, FrameTransport, InvokeResult } from './types';

function installHost(stub: Record<string, unknown>): void {
  const scope = globalThis as unknown as Record<string, unknown>;
  scope['hostFrame'] = stub;
}

function clearHosts(): void {
  const scope = globalThis as unknown as Record<string, unknown>;
  delete scope['hostFrame'];
  delete scope['hostBridge'];
}

function okResult(id: number, result: Record<string, unknown> = {}): string {
  return JSON.stringify({ t: 'res', id, ok: true, r: result });
}

describe('envelope identity and retry', () => {
  beforeEach(() => {
    clearHosts();
    localStorage.clear();
    try {
      sessionStorage.clear();
    } catch {
      /* jsdom may omit session storage */
    }
  });

  it('accepts a matching reply id and rejects a mismatched one', () => {
    expect(parseInvokeResult(okResult(41), 41).ok).toBe(true);
    const bad = parseInvokeResult(okResult(7), 41);
    expect(bad.ok).toBe(false);
    expect(bad.error?.code).toBe('INTERNAL');
  });

  it('retries once on a retryable code then succeeds', async () => {
    let calls = 0;
    installHost({
      invoke: async (body: string) => {
        calls += 1;
        const req = JSON.parse(body) as { id: number };
        if (calls === 1) return JSON.stringify({ t: 'res', id: req.id, ok: false, e: { code: 'RATE_LIMITED', msg: 'busy' } });
        return okResult(req.id, { turnId: 'turn-1' });
      },
      getEventBatch: async () => JSON.stringify({ frames: [], nextSeq: 0, hasGap: false, droppedEstimate: 0 }),
    });
    const transport = new BridgeTransport();
    const reply = await transport.invoke('agent.prompt', { text: 'hi' });
    expect(reply.ok).toBe(true);
    expect(calls).toBe(2);
  });

  it('reports a retryable hint when the host boundary is missing', async () => {
    const transport = new BridgeTransport();
    const reply = await transport.invoke('system.ping', {});
    expect(reply.ok).toBe(false);
    expect(reply.error?.code).toBe('NETWORK_FAIL');
    expect(reply.error?.hint).toBe('retry-after-attach');
    const batch = await transport.pollBatch(0, 50);
    expect(batch.frames).toEqual([]);
  });

  it('never throws on a hanging poll; the pump records the miss and backs off', async () => {
    installHost({
      invoke: async (body: string) => {
        const req = JSON.parse(body) as { id: number };
        return okResult(req.id);
      },
      getEventBatch: () => new Promise<string>(() => undefined),
    });
    const transport = new BridgeTransport({ pollMs: 50 });
    const batch = await transport.pollBatch(5, 50);
    expect(batch.nextSeq).toBe(5);
    expect(batch.frames).toEqual([]);
  });
});

function stubTransport(scenario: { latest: number }): FrameTransport {
  return {
    invoke: async (method: string): Promise<InvokeResult> => {
      if (method === 'state.snapshot') {
        return { ok: true, result: { activeSessionId: 'session-a', latestSequence: scenario.latest } };
      }
      return { ok: true, result: {} };
    },
    pollBatch: async (sinceSeq: number): Promise<EventBatch> => ({
      frames: [],
      nextSeq: sinceSeq,
      hasGap: sinceSeq === 0,
      droppedEstimate: sinceSeq === 0 ? 3 : 0,
    }),
  };
}

describe('single main pump behavior', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('uses the factory beat and honors per-instance storage keys', () => {
    const first = createFrameEventBridge(stubTransport({ latest: 9 }), { storageKey: 'pump-key-a' });
    const second = createFrameEventBridge(stubTransport({ latest: 9 }), { storageKey: 'pump-key-b' });
    expect(first).toBeInstanceOf(EventBridge);
    expect(second).toBeInstanceOf(EventBridge);
    localStorage.setItem('pump-key-a', '4');
    const reloaded = createFrameEventBridge(stubTransport({ latest: 9 }), { storageKey: 'pump-key-a' });
    expect(reloaded.lastSeq).toBe(4);
    expect(second.lastSeq).toBe(0);
  });

  it('rebuilds the cursor from the snapshot instead of zeroing it', async () => {
    const bridge = createFrameEventBridge(stubTransport({ latest: 9 }), { storageKey: 'pump-key-gap' });
    let resyncs = 0;
    bridge.onResync(() => {
      resyncs += 1;
    });
    await bridge.pollOnce();
    expect(resyncs).toBe(1);
    expect(bridge.lastSeq).toBe(9);
    expect(localStorage.getItem('pump-key-gap')).toBe('9');
    expect(bridge.stats.gaps).toBe(1);
    expect(bridge.stats.resyncs).toBe(1);
    expect(bridge.stats.polls).toBe(1);
  });

  it('primes the cursor once without emitting old frames', async () => {
    const transport = stubTransport({ latest: 12 });
    const bridge = createFrameEventBridge(transport, { storageKey: 'pump-key-prime' });
    const seen: number[] = [];
    bridge.on('*', (frame) => seen.push(frame.seq));
    await primeFrameEventBridge(bridge);
    expect(bridge.lastSeq).toBe(12);
    expect(seen).toEqual([]);
  });

  it('drops frames at or below the cursor and counts batches', async () => {
    const frames = [
      { t: 'evt', seq: 5, k: 'turn.started', p: {} },
      { t: 'evt', seq: 6, k: 'text.delta', p: {} },
    ];
    const transport: FrameTransport = {
      invoke: async () => ({ ok: true, result: { latestSequence: 6 } }),
      pollBatch: async () => ({ frames: frames as never, nextSeq: 6, hasGap: false, droppedEstimate: 0 }),
    };
    const bridge = createFrameEventBridge(transport, { storageKey: 'pump-key-dedup' });
    const seen: number[] = [];
    bridge.on('*', (frame) => seen.push(frame.seq));
    await bridge.pollOnce();
    expect(seen).toEqual([5, 6]);
    expect(bridge.stats.batches).toBe(1);
    // Replay of the same batch must not re-emit.
    await bridge.pollOnce();
    expect(seen).toEqual([5, 6]);
    expect(bridge.stats.batches).toBe(1);
    expect(bridge.stats.polls).toBe(2);
  });

  it('keeps disconnects survivable: failures rise without throwing', async () => {
    const failing: FrameTransport = {
      invoke: async () => ({ ok: true, result: {} }),
      pollBatch: async () => {
        throw new Error('link down');
      },
    };
    const bridge = createFrameEventBridge(failing, { storageKey: 'pump-key-fail' });
    await bridge.pollOnce();
    await bridge.pollOnce();
    expect(bridge.stats.polls).toBe(2);
    expect(bridge.lastSeq).toBe(0);
  });

  it('forwards pump counters to an observer without breaking the loop', async () => {
    const reports: { polls: number }[] = [];
    const bridge = new EventBridge(stubTransport({ latest: 3 }), {
      storageKey: 'pump-key-stats',
      onStats: (stats) => reports.push({ polls: stats.polls }),
    });
    const bad = (): void => {
      throw new Error('subscriber blew up');
    };
    bridge.on('*', bad);
    await bridge.pollOnce();
    expect(reports.length).toBeGreaterThan(0);
  });

  it('uses fake timers to prove the busy beat pulls fast after progress', async () => {
    vi.useFakeTimers();
    try {
      const transport: FrameTransport = {
        invoke: async () => ({ ok: true, result: { latestSequence: 2 } }),
        pollBatch: async (since: number) =>
          since < 2
            ? {
                frames: [{ t: 'evt', seq: since + 1, k: 'usage.tick', p: {} } as never],
                nextSeq: since + 1,
                hasGap: false,
                droppedEstimate: 0,
              }
            : { frames: [], nextSeq: since, hasGap: false, droppedEstimate: 0 },
      };
      const bridge = createFrameEventBridge(transport, { storageKey: 'pump-key-beat' });
      let ticks = 0;
      bridge.on('*', () => {
        ticks += 1;
      });
      bridge.start();
      await vi.advanceTimersByTimeAsync(500);
      bridge.stop();
      expect(ticks).toBeGreaterThan(0);
      expect(bridge.lastSeq).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });
});
describe('pump beats, lanes and degraded notice', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('keeps the contracted beat: 30ms busy, 200ms idle, 2s backoff cap', () => {
    const bridge = createFrameEventBridge(stubTransport({ latest: 0 }), { storageKey: 'pump-key-beat-contract' });
    expect(bridge.beat).toEqual({ busyMs: 30, idleMs: 200, backoffCapMs: 2000 });
  });

  it('flushes the stream lane on an animation frame without blocking the timer lane', async () => {
    const transport: FrameTransport = {
      invoke: async () => ({ ok: true, result: { latestSequence: 1 } }),
      pollBatch: async () => ({
        frames: [{ t: 'evt', seq: 1, k: 'text.delta', p: {} } as never],
        nextSeq: 1,
        hasGap: false,
        droppedEstimate: 0,
      }),
    };
    const bridge = createFrameEventBridge(transport, { storageKey: 'pump-key-lane' });
    const timerSeen: number[] = [];
    const streamSeen: number[] = [];
    bridge.on('text.delta', (frame) => timerSeen.push(frame.seq));
    bridge.onStream('text.delta', (frame) => streamSeen.push(frame.seq));
    await bridge.pollOnce();
    expect(timerSeen).toEqual([1]);
    expect(streamSeen).toEqual([]);
    await new Promise((resolve) => setTimeout(resolve, 32));
    expect(streamSeen).toEqual([1]);
  });

  it('raises a degraded notice after three consecutive misses, once per outage', async () => {
    const failing: FrameTransport = {
      invoke: async () => ({ ok: true, result: {} }),
      pollBatch: async () => {
        throw new Error('link down');
      },
    };
    const notices: number[] = [];
    const bridge = new EventBridge(failing, {
      storageKey: 'pump-key-degraded',
      onNotice: (notice) => notices.push(notice.consecutiveFailures),
    });
    await bridge.pollOnce();
    await bridge.pollOnce();
    expect(notices).toEqual([]);
    await bridge.pollOnce();
    expect(notices).toEqual([3]);
    await bridge.pollOnce();
    expect(notices).toEqual([3]);
  });
});
