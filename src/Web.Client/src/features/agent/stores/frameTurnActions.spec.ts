import { describe, expect, it } from 'vitest';
import type { ContractMethod } from '@/contract/agent';
import type { EventBatch, FrameTransport, InvokeResult } from '@/transport/types';
import { TurnProjectionBoard } from './turnProjection';
import { FrameTurnActions } from './frameTurnActions';

class RecordingTransport implements FrameTransport {
  calls: Array<{ method: ContractMethod; params: Record<string, unknown> }> = [];
  handler: (method: ContractMethod, params: Record<string, unknown>) => InvokeResult = () => ({ ok: true, result: { turnId: 'turn-1' } });

  async invoke(method: ContractMethod, params: Record<string, unknown> = {}): Promise<InvokeResult> {
    this.calls.push({ method, params });
    return this.handler(method, params);
  }

  async pollBatch(sinceSeq: number): Promise<EventBatch> {
    return { frames: [], nextSeq: sinceSeq, hasGap: false, droppedEstimate: 0 };
  }
}

describe('frame turn actions send edge', () => {
  it('refuses empty, compacting, budget-blocked and reentrant sends with stable copy', async () => {
    const transport = new RecordingTransport();
    const actions = new FrameTurnActions(transport);
    const empty = await actions.prompt({ sessionId: 'sess-1', text: '   ' });
    expect(empty.ok).toBe(false);
    expect(empty.code).toBe('INVALID_PARAMS');

    actions.noteCompacting('sess-1', true);
    const compacting = await actions.prompt({ sessionId: 'sess-1', text: 'hi' });
    expect(compacting.ok).toBe(false);
    expect(compacting.code).toBe('COMPACTING');
    expect(compacting.message).toContain('压缩中');
    actions.noteCompacting('sess-1', false);

    actions.noteBudgetBlocked('sess-1', true);
    const blocked = await actions.prompt({ sessionId: 'sess-1', text: 'hi' });
    expect(blocked.ok).toBe(false);
    expect(blocked.code).toBe('RATE_LIMITED');
    expect(blocked.message).toContain('压缩');
    actions.noteBudgetBlocked('sess-1', false);

    const first = await actions.prompt({ sessionId: 'sess-1', text: 'hi' });
    expect(first.ok).toBe(true);
    const second = await actions.prompt({ sessionId: 'sess-1', text: 'again' });
    expect(second.ok).toBe(false);
    expect(second.code).toBe('TURN_IN_PROGRESS');
    expect(second.message).toContain('requestId=');
    expect(actions.stats().reentryRefusals).toBe(1);
    expect(actions.stats().sendAttempts).toBe(1);
    actions.settle('sess-1');
    expect(actions.isBusy('sess-1')).toBe(false);
  });

  it('issues unique request keys and full prompt payloads', async () => {
    const transport = new RecordingTransport();
    const actions = new FrameTurnActions(transport);
    await actions.prompt({ sessionId: 'sess-1', text: 'one', needsConfirm: true, providerId: 'prov', modelId: 'model', timeoutSec: 60 });
    actions.settle('sess-1');
    await actions.prompt({ sessionId: 'sess-1', text: 'two' });
    const keys = transport.calls.map((call) => String(call.params['requestId'] ?? ''));
    expect(keys[0]).not.toBe('');
    expect(keys[0]).not.toBe(keys[1]);
    expect(transport.calls[0]?.params['needsConfirm']).toBe(true);
    expect(transport.calls[0]?.params['providerId']).toBe('prov');
    expect(transport.calls[0]?.params['modelId']).toBe('model');
    expect(transport.calls[0]?.params['timeoutSec']).toBe(60);
  });

  it('maps backend rejections and clears the busy mark on instant failure', async () => {
    const transport = new RecordingTransport();
    transport.handler = () => ({ ok: false, error: { code: 'PROVIDER_AUTH', msg: 'no key' } });
    const actions = new FrameTurnActions(transport);
    const failed = await actions.prompt({ sessionId: 'sess-1', text: 'hi' });
    expect(failed.ok).toBe(false);
    expect(failed.code).toBe('PROVIDER_AUTH');
    expect(failed.retry).toBe(false);
    expect(actions.isBusy('sess-1')).toBe(false);
  });

  it('retries with session scope only and settles on terminal events', async () => {
    const transport = new RecordingTransport();
    transport.handler = (method) =>
      method === 'agent.retry' ? { ok: true, result: { turnId: 'turn-9' } } : { ok: true, result: { turnId: 'turn-1' } };
    const actions = new FrameTurnActions(transport);
    const retried = await actions.retry('sess-1');
    expect(retried.ok).toBe(true);
    expect(retried.turnId).toBe('turn-9');
    const retryCall = transport.calls.find((call) => call.method === 'agent.retry');
    expect(retryCall?.params).toEqual({ sessionId: 'sess-1' });
    expect(actions.observeTerminal({ t: 'evt', seq: 3, k: 'turn.done', p: { turnId: 'turn-9', sessionId: 'sess-1' } })).toBe(true);
    expect(actions.isBusy('sess-1')).toBe(false);
  });

  it('cancels by turn id and reports an unsignaled miss as cancel-failed', async () => {
    const transport = new RecordingTransport();
    transport.handler = () => ({ ok: false, error: { code: 'NOT_FOUND', msg: 'unknown turn' } });
    const actions = new FrameTurnActions(transport);
    const missed = await actions.cancel('ghost-turn');
    expect(missed.ok).toBe(false);
    expect(missed.signaled).toBe(false);
    expect(missed.code).toBe('NOT_FOUND');
  });

  it('answers with the optional call key for concurrent confirmations', async () => {
    const transport = new RecordingTransport();
    const actions = new FrameTurnActions(transport);
    const done = await actions.answer('turn-1', true, 'call-1');
    expect(done.ok).toBe(true);
    expect(transport.calls[0]?.params).toMatchObject({ turnId: 'turn-1', approved: true, rejected: false, toolCallId: 'call-1' });
  });

  it('resolves the live turn of a session for the action dock', () => {
    const board = new TurnProjectionBoard();
    const transport = new RecordingTransport();
    const actions = new FrameTurnActions(transport);
    expect(actions.activeTurnOf('sess-1', board)).toBe('');
    board.observe({ t: 'evt', seq: 1, k: 'turn.started', p: { turnId: 'turn-live', sessionId: 'sess-1' } });
    expect(actions.activeTurnOf('sess-1', board)).toBe('turn-live');
    board.observe({ t: 'evt', seq: 2, k: 'turn.done', p: { turnId: 'turn-live', sessionId: 'sess-1' } });
    expect(actions.activeTurnOf('sess-1', board)).toBe('');
  });
});
