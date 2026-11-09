import { beforeEach, describe, expect, it } from 'vitest';
import { BridgeTransport, parseEventBatch, parseInvokeResult } from './bridgeTransport';
import { FramePump } from './framePump';
import {
  DISABLE_PUMP_KEY,
  LegacyTransport,
  NEW_ENGINE_KEY,
  createTransport,
  isEventPumpDisabled,
  isNewEngineEnabled,
  setNewEngineEnabled,
} from './legacy';
import { MockTransport } from './mockTransport';

function frameHostStubs() {
  return {
    invoke: async (body: string) => {
      const request = JSON.parse(body) as { id: number; m: string };
      return JSON.stringify({ t: 'res', id: request.id, ok: true, r: { method: request.m } });
    },
    getEventBatch: async (since: number, _take: number) =>
      JSON.stringify({
        frames: [{ t: 'evt', seq: since + 1, k: 'turn.started', p: { turnId: 'turn-1' } }],
        nextSeq: since + 1,
        hasGap: false,
        droppedEstimate: 0,
      }),
  };
}

describe('frame boundary selection and fallback', () => {
  beforeEach(() => {
    localStorage.clear();
    try {
      sessionStorage.clear();
    } catch {
      /* ignore */
    }
    const scope = globalThis as unknown as Record<string, unknown>;
    delete scope['hostFrame'];
    delete scope['hostBridge'];
    delete scope['__nativeHostFrame'];
    delete scope['CefSharp'];
  });

  it('prefers the frame object and falls back to the legacy facade object', async () => {
    const scope = globalThis as unknown as Record<string, unknown>;
    scope['hostBridge'] = frameHostStubs();
    const fallback = new BridgeTransport();
    const viaFallback = await fallback.invoke('system.ping', {});
    expect(viaFallback.ok).toBe(true);
    expect(viaFallback.result?.['method']).toBe('system.ping');

    scope['hostFrame'] = {
      invoke: async (body: string) => {
        const request = JSON.parse(body) as { id: number };
        return JSON.stringify({ t: 'res', id: request.id, ok: true, r: { frame: true } });
      },
      getEventBatch: async () => JSON.stringify({ frames: [], nextSeq: 0, hasGap: false, droppedEstimate: 0 }),
    };
    const preferred = new BridgeTransport();
    const viaFrame = await preferred.invoke('system.ping', {});
    expect(viaFrame.result?.['frame']).toBe(true);
  });

  it('reports an unavailable boundary without throwing', async () => {
    const transport = new BridgeTransport();
    const reply = await transport.invoke('system.ping', {});
    expect(reply.ok).toBe(false);
    expect(reply.error?.code).toBe('NETWORK_FAIL');
    const batch = await transport.pollBatch(0, 500);
    expect(batch.frames).toEqual([]);
    expect(batch.hasGap).toBe(false);
  });

  it('keeps the whole-chain switch on by default and round-trips it', () => {
    expect(isNewEngineEnabled()).toBe(true);
    setNewEngineEnabled(false);
    expect(localStorage.getItem(NEW_ENGINE_KEY)).toBe('false');
    expect(isNewEngineEnabled()).toBe(false);
    setNewEngineEnabled(true);
    expect(isNewEngineEnabled()).toBe(true);
    expect(isEventPumpDisabled()).toBe(false);
    localStorage.setItem(DISABLE_PUMP_KEY, 'true');
    expect(isEventPumpDisabled()).toBe(true);
  });

  it('builds the requested backing without mixing chains', () => {
    expect(createTransport('mock')).toBeInstanceOf(MockTransport);
    expect(createTransport('bridge')).toBeInstanceOf(BridgeTransport);
  });

  it('isolates malformed boundary payloads', () => {
    expect(parseInvokeResult('not-json').ok).toBe(false);
    expect(parseInvokeResult('{"t":"res"}').error?.code).toBe('INTERNAL');
    const batch = parseEventBatch('broken', 4);
    expect(batch.nextSeq).toBe(4);
    expect(batch.frames).toEqual([]);
  });

  it('rolls back through the whole-chain adapter as one flip', async () => {
    const adapter = new LegacyTransport(async () => JSON.stringify({ ok: true, r: { legacy: true } }));
    const reply = await adapter.invoke('system.ping', {});
    expect(reply.ok).toBe(true);
    const batch = await adapter.pollBatch(3);
    expect(batch.nextSeq).toBe(3);
    expect(batch.hasGap).toBe(false);
  });
});

describe('frame pump', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('delivers frames and persists the cursor', async () => {
    const transport = new MockTransport();
    transport.seedScenario('pump');
    const pump = new FramePump(transport, 'frame-pump-cursor-a');
    const seen: string[] = [];
    pump.onFrame((frame) => seen.push(frame.k));
    const batch = await pump.pullOnce();
    expect(batch?.hasGap).toBe(false);
    expect(seen).toContain('session.updated');
    expect(pump.lastSeq).toBeGreaterThan(0);
    expect(localStorage.getItem('frame-pump-cursor-a')).toBe(String(pump.lastSeq));
  });

  it('pauses on a gap and resumes from the rebuilt cursor', async () => {
    const transport = new MockTransport();
    const pump = new FramePump(transport, 'frame-pump-cursor-b');
    let gaps = 0;
    pump.onGap(() => (gaps += 1));
    const forged = {
      invoke: () => Promise.resolve({ ok: true, result: {} }),
      pollBatch: () => Promise.resolve({ frames: [], nextSeq: 0, hasGap: true, droppedEstimate: 2 }),
    };
    const stale = new FramePump(forged as unknown as MockTransport, 'frame-pump-cursor-c');
    let notified = 0;
    stale.onGap(() => (notified += 1));
    await stale.pullOnce();
    expect(notified).toBe(1);
    expect(gaps).toBe(0);
    pump.resumeFrom(12);
    expect(pump.lastSeq).toBe(12);
  });

  it('skips foreign frames and survives bad subscribers', async () => {
    const transport = new MockTransport();
    const pump = new FramePump(transport, 'frame-pump-cursor-d');
    pump.onFrame(() => {
      throw new Error('subscriber blew up');
    });
    const batch = await pump.pullOnce();
    expect(batch).not.toBeNull();
  });
});
