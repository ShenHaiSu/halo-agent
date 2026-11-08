import { describe, expect, it } from 'vitest';
import { MockTransport } from './mockTransport';
import { EventBridge } from '../stores/eventBridge';

describe('mock main flow replay', () => {
  it('runs ping, new session, prompt and batched poll', async () => {
    const transport = new MockTransport();
    const ping = await transport.invoke('system.ping', {});
    expect(ping.ok).toBe(true);

    const created = await transport.invoke('session.new', { title: 'demo' });
    expect(created.ok).toBe(true);
    const sessionId = created.result?.['sessionId'] as string;
    expect(sessionId).toMatch(/^mock-session-/);

    const prompted = await transport.invoke('agent.prompt', { sessionId, text: 'hello' });
    expect(prompted.ok).toBe(true);
    const turnId = prompted.result?.['turnId'] as string;
    expect(turnId).toMatch(/^mock-turn-/);

    const batch = await transport.pollBatch(0, 500);
    expect(batch.hasGap).toBe(false);
    const kinds = batch.frames.map((f) => f.k);
    expect(kinds).toContain('turn.started');
    expect(kinds).toContain('text.delta');
    expect(kinds).toContain('text.done');
    expect(kinds).toContain('turn.done');
    const seqs = batch.frames.map((f) => f.seq);
    expect([...seqs].sort((a, b) => a - b)).toEqual(seqs);
  });

  it('delivers new turns through the event bridge', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const bridge = new EventBridge(transport);
    const seen: string[] = [];
    bridge.on('turn.done', (frame) => seen.push(String((frame.p as Record<string, unknown>)?.['turnId'] ?? '')));
    await transport.invoke('agent.prompt', { sessionId, text: 'hi' });
    await bridge.pollOnce();
    expect(seen.length).toBe(1);
    expect(bridge.lastSeq).toBeGreaterThan(0);
  });

  it('rejects prompt without text', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', {});
    const sessionId = created.result?.['sessionId'] as string;
    const bad = await transport.invoke('agent.prompt', { sessionId });
    expect(bad.ok).toBe(false);
    expect(bad.error?.code).toBe('INVALID_PARAMS');
  });

  it('recovers from an injected gap through resync', async () => {
    const transport = new MockTransport();
    transport.seedScenario('main-flow');
    const bridge = new EventBridge(transport);
    let resynced = 0;
    bridge.onResync(() => (resynced += 1));
    const forged = {
      invoke: () => Promise.resolve({ ok: true, result: {} }),
      pollBatch: () => Promise.resolve({ frames: [], nextSeq: 0, hasGap: true, droppedEstimate: 3 }),
    };
    const stale = new EventBridge(forged as unknown as typeof transport);
    stale.onResync(() => (resynced += 1));
    await stale.pollOnce();
    expect(resynced).toBe(1);
    await bridge.pollOnce();
    expect(bridge.lastSeq).toBeGreaterThan(0);
  });
});
