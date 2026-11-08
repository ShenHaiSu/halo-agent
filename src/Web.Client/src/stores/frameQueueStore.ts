import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import type { ContractEventFrame } from '@/contract/agent';
import type { FrameTransport } from '@/transport/types';

export interface PendingConfirmation {
  turnId: string;
  toolCallId: string;
  toolName: string;
}

/**
 * Queue and confirmation view over the frame boundary. The backend owns the
 * queue and suspends turns that wait for a human answer; this store keeps
 * only the pending confirmation surfaced by `tool.confirm` plus thin
 * invoke helpers. Form state stays local, truth stays remote.
 */
export const useFrameQueueStore = defineStore('frameQueue', () => {
  const transport = ref<FrameTransport | null>(null);
  const pending = ref<PendingConfirmation | null>(null);
  const lastError = ref('');

  const hasPending = computed(() => pending.value !== null);

  function bind(frameTransport: FrameTransport): void {
    transport.value = frameTransport;
  }

  function reset(): void {
    pending.value = null;
    lastError.value = '';
  }

  /** Feed one event frame; returns true when it completed the pending item. */
  function observe(frame: ContractEventFrame): boolean {
    if (!frame || frame.t !== 'evt') return false;
    const params = (frame.p ?? {}) as Record<string, unknown>;
    if (frame.k === 'tool.confirm') {
      const turnId = typeof params['turnId'] === 'string' ? (params['turnId'] as string) : '';
      if (turnId === '') return false;
      pending.value = {
        turnId,
        toolCallId: typeof params['toolCallId'] === 'string' ? (params['toolCallId'] as string) : '',
        toolName: typeof params['toolName'] === 'string' ? (params['toolName'] as string) : '',
      };
      return false;
    }
    if (frame.k === 'turn.done' || frame.k === 'turn.failed' || frame.k === 'turn.cancelled') {
      const turnId = typeof params['turnId'] === 'string' ? (params['turnId'] as string) : '';
      if (pending.value && turnId !== '' && turnId === pending.value.turnId) {
        pending.value = null;
        return true;
      }
    }
    return false;
  }

  async function enqueue(params: Record<string, unknown>): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const reply = await channel.invoke('agent.queue', params);
    if (!reply.ok) {
      lastError.value = reply.error?.msg || 'Queue failed.';
      return false;
    }
    lastError.value = '';
    return true;
  }

  async function dequeue(params: Record<string, unknown>): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const reply = await channel.invoke('agent.unqueue', params);
    if (!reply.ok) {
      lastError.value = reply.error?.msg || 'Dequeue failed.';
      return false;
    }
    lastError.value = '';
    return true;
  }

  async function answer(approved: boolean): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const current = pending.value;
    if (!current) {
      lastError.value = 'No pending confirmation.';
      return false;
    }
    const reply = await channel.invoke('agent.answer', {
      turnId: current.turnId,
      toolCallId: current.toolCallId,
      approved,
      rejected: !approved,
    });
    if (!reply.ok) {
      lastError.value = reply.error?.msg || 'Answer failed.';
      return false;
    }
    lastError.value = '';
    if (!approved) pending.value = null;
    return true;
  }

  async function cancelTurn(turnId: string): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const reply = await channel.invoke('agent.cancel', { turnId });
    if (!reply.ok) {
      lastError.value = reply.error?.msg || 'Cancel failed.';
      return false;
    }
    lastError.value = '';
    return true;
  }

  return {
    pending,
    lastError,
    hasPending,
    bind,
    reset,
    observe,
    enqueue,
    dequeue,
    answer,
    cancelTurn,
  };
});
