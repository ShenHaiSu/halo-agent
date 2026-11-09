import { describe, expect, it } from 'vitest';
import type { ContractEventKind } from '../contract/agent';
import { MockTransport } from './mockTransport';

function isTerminal(kind: ContractEventKind): boolean {
  return kind === 'turn.done' || kind === 'turn.failed' || kind === 'turn.cancelled';
}

describe('frame confirmation gate round trip', () => {
  it('holds a turn on confirmation and settles it on approval', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const prompted = await transport.invoke('agent.prompt', { sessionId, text: 'edit a.txt', needsConfirm: true });
    expect(prompted.ok).toBe(true);
    const turnId = prompted.result?.['turnId'] as string;

    const batch = await transport.pollBatch(0, 500);
    const kinds = batch.frames.map((frame) => frame.k);
    expect(kinds).toContain('tool.confirm');

    const approved = await transport.invoke('agent.answer', { turnId, approved: true, rejected: false });
    expect(approved.ok).toBe(true);
    const tail = await transport.pollBatch(batch.nextSeq, 500);
    const tailKinds = tail.frames.map((frame) => frame.k);
    expect(tailKinds).toContain('tool.end');
    expect(tailKinds).toContain('turn.done');
    expect(tail.frames.every((frame) => frame.k === 'tool.end' || isTerminal(frame.k))).toBe(true);
  });

  it('settles a rejection as a cancelled turn with the rejected reason', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const prompted = await transport.invoke('agent.prompt', { sessionId, text: 'edit b.txt', needsConfirm: true });
    const turnId = prompted.result?.['turnId'] as string;
    const denied = await transport.invoke('agent.answer', { turnId, approved: false, rejected: true });
    expect(denied.ok).toBe(true);
    const batch = await transport.pollBatch(0, 500);
    const cancelled = batch.frames.find((frame) => frame.k === 'turn.cancelled');
    expect(cancelled?.p?.['reason']).toBe('rejected');
    expect(cancelled?.p?.['signaled']).toBe(true);
  });

  it('answers nothing without a pending confirmation', async () => {
    const transport = new MockTransport();
    const missed = await transport.invoke('agent.answer', { turnId: 'ghost-turn', approved: true });
    expect(missed.ok).toBe(false);
    expect(missed.error?.code).toBe('NOT_FOUND');
  });

  it('reports an unknown cancel as not found while still emitting an unsignaled event', async () => {
    const transport = new MockTransport();
    const missed = await transport.invoke('agent.cancel', { turnId: 'ghost-turn' });
    expect(missed.ok).toBe(false);
    expect(missed.error?.code).toBe('NOT_FOUND');
    const batch = await transport.pollBatch(0, 500);
    const cancelled = batch.frames.find((frame) => frame.k === 'turn.cancelled');
    expect(cancelled?.p?.['signaled']).toBe(false);
  });

  it('reports a repeated cancel of a settled turn as already cancelled', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const prompted = await transport.invoke('agent.prompt', { sessionId, text: 'hi' });
    const turnId = prompted.result?.['turnId'] as string;
    const again = await transport.invoke('agent.cancel', { turnId });
    expect(again.ok).toBe(false);
    expect(again.error?.code).toBe('CANCELLED');
  });

  it('emits a failure entry with the machine code for the outcome card', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const prompted = await transport.invoke('agent.prompt', { sessionId, text: 'hi' });
    const turnId = prompted.result?.['turnId'] as string;
    transport.failTurn(turnId, 'NETWORK_FAIL', 'timeout after 30s');
    const batch = await transport.pollBatch(0, 500);
    const failed = batch.frames.find((frame) => frame.k === 'turn.failed');
    expect(failed?.p?.['reason']).toBe('NETWORK_FAIL');
    expect(failed?.p?.['hint']).toBe('timeout after 30s');
  });

  it('expires a confirmation into a timeout cancel', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const prompted = await transport.invoke('agent.prompt', { sessionId, text: 'edit c.txt', needsConfirm: true });
    const turnId = prompted.result?.['turnId'] as string;
    transport.expireConfirm(turnId);
    const batch = await transport.pollBatch(0, 500);
    const cancelled = batch.frames.find((frame) => frame.k === 'turn.cancelled' && frame.p?.['reason'] === 'timeout');
    expect(cancelled).toBeDefined();
    const late = await transport.invoke('agent.answer', { turnId, approved: true });
    expect(late.ok).toBe(false);
  });

  it('replays a retry with session scope and marks its origin', async () => {
    const transport = new MockTransport();
    const created = await transport.invoke('session.new', { title: 'demo' });
    const sessionId = created.result?.['sessionId'] as string;
    const retried = await transport.invoke('agent.retry', { sessionId });
    expect(retried.ok).toBe(true);
    const batch = await transport.pollBatch(0, 500);
    const started = batch.frames.find((frame) => frame.k === 'turn.started');
    expect(started?.p?.['retriedFrom']).toBeDefined();
    const missing = await transport.invoke('agent.retry', { sessionId: 'ghost-session' });
    expect(missing.error?.code).toBe('NOT_FOUND');
  });

  it('shapes retry, cancel and answer payloads with the optional call key', () => {
    expect({ t: 'req', id: 1, m: 'agent.retry', p: { sessionId: 'sess-1' } }).toEqual({
      t: 'req',
      id: 1,
      m: 'agent.retry',
      p: { sessionId: 'sess-1' },
    });
    const answerParams: Record<string, unknown> = { turnId: 'turn-1', approved: true, rejected: false, toolCallId: 'call-1' };
    expect(answerParams).toMatchObject({ turnId: 'turn-1', approved: true, toolCallId: 'call-1' });
    const deniedParams: Record<string, unknown> = { turnId: 'turn-1', approved: false, rejected: true };
    expect(deniedParams).toMatchObject({ approved: false, rejected: true });
  });
});
