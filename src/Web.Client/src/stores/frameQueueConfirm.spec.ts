import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { ContractEventFrame } from '@/contract/agent';
import { MockTransport } from '@/transport/mockTransport';
import { CONFIRM_TIMEOUT_SEC, useFrameQueueStore } from './frameQueueStore';

let seq = 200;
function frame(kind: ContractEventFrame['k'], params: Record<string, unknown>): ContractEventFrame {
  seq += 1;
  return { t: 'evt', seq, k: kind, p: params };
}

describe('frame queue concurrent confirmations', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('keeps concurrent confirmations without loss', () => {
    const store = useFrameQueueStore();
    store.observe(frame('tool.confirm', { turnId: 'turn-a', toolCallId: 'call-1', toolName: 'edit.apply' }));
    store.observe(frame('tool.confirm', { turnId: 'turn-a', toolCallId: 'call-2', toolName: 'write_file' }));
    expect(store.pendingCount).toBe(2);
    expect(store.pendingOf('turn-a', 'call-1')?.toolName).toBe('edit.apply');
    expect(store.pendingOf('turn-a', 'call-2')?.toolName).toBe('write_file');
    const completed = store.observe(frame('turn.done', { turnId: 'turn-a', toolCallId: 'call-1' }));
    expect(completed).toBe(true);
    expect(store.pendingCount).toBe(1);
    expect(store.hasPending).toBe(true);
  });

  it('carries the ask-user chain fields for the modal', () => {
    const store = useFrameQueueStore();
    store.observe(
      frame('tool.confirm', {
        turnId: 'turn-b',
        toolCallId: 'call-9',
        toolName: 'ask_user',
        ticketId: 'ticket-1',
        sessionId: 'sess-1',
        timeoutSec: 120,
        questions: [{ id: 'q1', question: '继续吗？' }],
      })
    );
    const entry = store.pendingOf('turn-b', 'call-9');
    expect(entry?.ticketId).toBe('ticket-1');
    expect(entry?.timeoutSec).toBe(120);
    expect(entry?.questions).toEqual([{ id: 'q1', question: '继续吗？' }]);
  });

  it('rolls back an optimistic approval when the answer invoke fails', async () => {
    const store = useFrameQueueStore();
    store.bind(new MockTransport());
    store.observe(frame('tool.confirm', { turnId: 'turn-c', toolCallId: 'call-3', toolName: 'edit.apply' }));
    const transport = {
      invoke: async () => ({ ok: false as const, error: { code: 'NOT_FOUND' as const, msg: 'gone' } }),
      pollBatch: async () => ({ frames: [], nextSeq: 0, hasGap: false, droppedEstimate: 0 }),
    };
    store.bind(transport);
    expect(await store.answer(true, 'call-3')).toBe(false);
    expect(store.hasPending).toBe(true);
    expect(store.approveRollbacks).toBe(1);
  });

  it('clears a rejection locally at once without rollback accounting', async () => {
    const store = useFrameQueueStore();
    const transport = {
      invoke: async () => ({ ok: true as const, result: {} }),
      pollBatch: async () => ({ frames: [], nextSeq: 0, hasGap: false, droppedEstimate: 0 }),
    };
    store.bind(transport);
    store.observe(frame('tool.confirm', { turnId: 'turn-d', toolCallId: 'call-4', toolName: 'edit.apply' }));
    expect(await store.answer(false, 'call-4')).toBe(true);
    expect(store.hasPending).toBe(false);
    expect(store.approveRollbacks).toBe(0);
  });

  it('counts down with the backend window and expires to the timeout path', () => {
    const store = useFrameQueueStore();
    store.observe(frame('tool.confirm', { turnId: 'turn-e', toolCallId: 'call-5', toolName: 'ask_user' }));
    const entry = store.pendingOf('turn-e', 'call-5');
    expect(entry?.timeoutSec).toBe(CONFIRM_TIMEOUT_SEC);
    expect(entry).not.toBeNull();
    const now = Date.now();
    const left = store.remainingSec('turn-e', 'call-5', now);
    expect(left).toBeGreaterThan(0);
    expect(left).toBeLessThanOrEqual(CONFIRM_TIMEOUT_SEC);
    const expired = store.tick((entry as { deadlineAt: number }).deadlineAt + 1000);
    expect(expired).toHaveLength(1);
    expect(store.hasPending).toBe(false);
    expect(store.confirmTimeouts).toBe(1);
  });
});
