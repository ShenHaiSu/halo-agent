import { beforeEach, describe, expect, it } from 'vitest';
import { BridgeTransport } from './bridgeTransport';
import { cacheNativeFrame, ensureNativeFrame, getNativeFrame, resetNativeFrameForTest } from '../shared/bridge/nativeFrame';

function frameStub() {
  return {
    invoke: async (body: string) => {
      const request = JSON.parse(body) as { id: number };
      return JSON.stringify({ t: 'res', id: request.id, ok: true, r: { frame: true } });
    },
    getEventBatch: async () => JSON.stringify({ frames: [], nextSeq: 0, hasGap: false, droppedEstimate: 0 }),
  };
}

describe('native frame dual-bind', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    resetNativeFrameForTest();
    const scope = globalThis as unknown as Record<string, unknown>;
    delete scope['hostFrame'];
    delete scope['hostBridge'];
    delete scope['__nativeHostFrame'];
    delete scope['CefSharp'];
  });

  it('binds both objects once and survives one side failing', async () => {
    const scope = globalThis as unknown as Record<string, unknown>;
    const calls: string[] = [];
    scope['CefSharp'] = {
      BindObjectAsync: async (name: string) => {
        calls.push(name);
        if (name === 'hostBridge') throw new Error('bridge down');
        scope['hostFrame'] = frameStub();
      },
    };
    const usable = await ensureNativeFrame();
    expect(calls).toContain('hostBridge');
    expect(calls).toContain('hostFrame');
    expect(usable).not.toBeNull();
    // Once promise is cached.
    const again = await ensureNativeFrame();
    expect(again).toBe(usable);
    expect(calls.length).toBe(2);
  });

  it('ignores the wrapped facade and prefers the cached native frame', async () => {
    const scope = globalThis as unknown as Record<string, unknown>;
    const native = frameStub();
    cacheNativeFrame(native);
    scope['hostBridge'] = { __haloWrappedApi: true };
    scope['hostFrame'] = undefined;
    expect(getNativeFrame()).toBe(native);
    const reply = await new BridgeTransport().invoke('system.ping', {});
    expect(reply.ok).toBe(true);
    expect(reply.result?.['frame']).toBe(true);
  });

  it('does not mistake the wrapped facade for a boundary', async () => {
    const scope = globalThis as unknown as Record<string, unknown>;
    scope['hostBridge'] = { __haloWrappedApi: true, invoke: async () => '{}' };
    const reply = await new BridgeTransport().invoke('system.ping', {});
    expect(reply.ok).toBe(false);
    expect(reply.error?.code).toBe('NETWORK_FAIL');
  });
});
