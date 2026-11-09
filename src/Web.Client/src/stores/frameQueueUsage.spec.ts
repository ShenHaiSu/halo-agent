import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { ContractEventFrame } from '@/contract/agent';
import { MockTransport } from '@/transport/mockTransport';
import { useFrameQueueStore } from './frameQueueStore';
import { useFrameUsageStore } from './frameUsageStore';

let seq = 100;
function frame(kind: ContractEventFrame['k'], params: Record<string, unknown>): ContractEventFrame {
  seq += 1;
  return { t: 'evt', seq, k: kind, p: params };
}

describe('frame queue store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('surfaces a confirmation and clears it when the turn ends', () => {
    const store = useFrameQueueStore();
    expect(store.hasPending).toBe(false);
    store.observe(frame('tool.confirm', { turnId: 'turn-7', toolCallId: 'call-7', toolName: 'edit.apply' }));
    expect(store.hasPending).toBe(true);
    expect(store.pending?.toolName).toBe('edit.apply');
    const completed = store.observe(frame('turn.done', { turnId: 'turn-7' }));
    expect(completed).toBe(true);
    expect(store.hasPending).toBe(false);
  });

  it('routes queue calls through the bound transport', async () => {
    const store = useFrameQueueStore();
    expect(await store.enqueue({})).toBe(false);
    const transport = new MockTransport();
    store.bind(transport);
    expect(await store.enqueue({})).toBe(true);
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const prompted = await transport.invoke('agent.prompt', { sessionId, text: 'hi', needsConfirm: true });
    const turnId = prompted.result?.['turnId'] as string;
    expect(await store.cancelTurn(turnId)).toBe(true);
    expect(await store.answer(true)).toBe(false);
    expect(store.lastError).not.toBe('');
  });
});

describe('frame usage store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('accumulates tick deltas between queries', () => {
    const store = useFrameUsageStore();
    store.observe(frame('usage.tick', { inputTokens: 10, outputTokens: 4 }));
    store.observe(frame('usage.tick', { inputTokens: 2, outputTokens: 8, retryCount: 1 }));
    expect(store.totalTokens).toBe(24);
    expect(store.retryCount).toBe(1);
  });

  it('drops replayed sequences and per-turn duplicates', () => {
    const store = useFrameUsageStore();
    const first: ContractEventFrame = { t: 'evt', seq: 501, k: 'usage.tick', p: { turnId: 'turn-1', inputTokens: 10, outputTokens: 4 } };
    const replay: ContractEventFrame = { t: 'evt', seq: 501, k: 'usage.tick', p: { turnId: 'turn-1', inputTokens: 10, outputTokens: 4 } };
    const stale: ContractEventFrame = { t: 'evt', seq: 500, k: 'usage.tick', p: { turnId: 'turn-1', inputTokens: 10, outputTokens: 4 } };
    expect(store.observe(first)).toBe(true);
    expect(store.totalTokens).toBe(14);
    expect(store.observe(replay)).toBe(false);
    expect(store.observe(stale)).toBe(false);
    expect(store.totalTokens).toBe(14);
    expect(store.lastSeq).toBe(501);
  });

  it('keeps the budget bar on the legacy source with a degraded flag', () => {
    const store = useFrameUsageStore();
    expect(store.budgetSource).toBe('legacy');
    expect(store.degraded).toBe(false);
  });

  it('refreshes totals from the bound transport and clears them', async () => {
    const store = useFrameUsageStore();
    expect(await store.refresh()).toBe(false);
    store.bind(new MockTransport());
    expect(await store.refresh()).toBe(true);
    expect(await store.clear()).toBe(true);
    expect(store.totalTokens).toBe(0);
  });
});
