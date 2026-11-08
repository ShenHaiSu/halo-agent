import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import type { ContractEventFrame } from '@/contract/agent';
import type { FrameTransport } from '@/transport/types';

export interface PendingConfirmation {
  turnId: string;
  toolCallId: string;
  toolName: string;
  sessionId: string;
  argsPreview: string;
  policy: string;
  timeoutSec: number;
  ticketId: string;
  questions: unknown;
  arrivedAt: number;
  deadlineAt: number;
}

/** Default confirmation window; matches the backend ask gate default. */
export const CONFIRM_TIMEOUT_SEC = 300;
const MIN_TIMEOUT_SEC = 1;
const MAX_TIMEOUT_SEC = 3600;

function confirmKey(turnId: string, toolCallId: string): string {
  return `${turnId}::${toolCallId}`;
}

function readString(record: Record<string, unknown>, key: string): string {
  const value = record[key];
  return typeof value === 'string' ? value : '';
}

function readTimeoutSec(record: Record<string, unknown>): number {
  const value = record['timeoutSec'];
  if (typeof value !== 'number' || !Number.isFinite(value)) return CONFIRM_TIMEOUT_SEC;
  return Math.max(MIN_TIMEOUT_SEC, Math.min(MAX_TIMEOUT_SEC, Math.floor(value)));
}

/**
 * Queue and confirmation view over the frame boundary. The backend owns the
 * queue and suspends turns that wait for a human answer; this store keeps
 * the pending confirmations keyed by `(turnId, toolCallId)` plus thin
 * invoke helpers. Approvals clear optimistically and roll back on invoke
 * failure; rejections clear locally at once. Form state stays local, truth
 * stays remote.
 */
export const useFrameQueueStore = defineStore('frameQueue', () => {
  const transport = ref<FrameTransport | null>(null);
  const pendings = ref<Record<string, PendingConfirmation>>({});
  const lastError = ref('');
  const approveRollbacks = ref(0);
  const confirmTimeouts = ref(0);

  /** Legacy single-pending view: the oldest live confirmation, if any. */
  const pending = computed<PendingConfirmation | null>(() => {
    const list = Object.values(pendings.value).sort((a, b) => a.arrivedAt - b.arrivedAt);
    return list.length > 0 ? (list[0] as PendingConfirmation) : null;
  });
  const pendingList = computed<PendingConfirmation[]>(() => Object.values(pendings.value).sort((a, b) => a.arrivedAt - b.arrivedAt));
  const pendingCount = computed(() => Object.keys(pendings.value).length);
  const hasPending = computed(() => pending.value !== null);

  function bind(frameTransport: FrameTransport): void {
    transport.value = frameTransport;
  }

  function reset(): void {
    pendings.value = {};
    lastError.value = '';
    approveRollbacks.value = 0;
    confirmTimeouts.value = 0;
  }

  function pendingOf(turnId: string, toolCallId?: string): PendingConfirmation | null {
    const turn = (turnId || '').trim();
    if (!turn) return null;
    const call = (toolCallId || '').trim();
    if (call !== '') return pendings.value[confirmKey(turn, call)] ?? null;
    const match = Object.values(pendings.value).find((entry) => entry.turnId === turn);
    return match ?? null;
  }

  /** Seconds left before a confirmation expires; never negative. */
  function remainingSec(turnId: string, toolCallId: string | undefined, now: number): number {
    const entry = pendingOf(turnId, toolCallId);
    if (!entry) return 0;
    return Math.max(0, Math.ceil((entry.deadlineAt - now) / 1000));
  }

  /**
   * Expire confirmations past their deadline. Expired entries leave the
   * queue; the backend settles the turn as `turn.cancelled{reason:timeout}`.
   */
  function tick(now: number): PendingConfirmation[] {
    const expired: PendingConfirmation[] = [];
    for (const [key, entry] of Object.entries(pendings.value)) {
      if (now >= entry.deadlineAt) {
        expired.push(entry);
        delete pendings.value[key];
      }
    }
    if (expired.length > 0) confirmTimeouts.value += expired.length;
    return expired;
  }

  /** Feed one event frame; returns true when it completed a pending item. */
  function observe(frame: ContractEventFrame): boolean {
    if (!frame || frame.t !== 'evt') return false;
    const params = (frame.p ?? {}) as Record<string, unknown>;
    if (frame.k === 'tool.confirm') {
      const turnId = readString(params, 'turnId');
      if (turnId === '') return false;
      const toolCallId = readString(params, 'toolCallId');
      const timeoutSec = readTimeoutSec(params);
      const now = Date.now();
      pendings.value[confirmKey(turnId, toolCallId)] = {
        turnId,
        toolCallId,
        toolName: readString(params, 'toolName'),
        sessionId: readString(params, 'sessionId'),
        argsPreview: readString(params, 'argsPreview'),
        policy: readString(params, 'policy'),
        timeoutSec,
        ticketId: readString(params, 'ticketId'),
        questions: params['questions'] ?? params['questionYaml'] ?? null,
        arrivedAt: now,
        deadlineAt: now + timeoutSec * 1000,
      };
      return false;
    }
    if (frame.k === 'turn.done' || frame.k === 'turn.failed' || frame.k === 'turn.cancelled') {
      const turnId = readString(params, 'turnId');
      if (turnId === '') return false;
      const callId = readString(params, 'toolCallId');
      let completed = false;
      for (const [key, entry] of Object.entries(pendings.value)) {
        if (entry.turnId !== turnId) continue;
        if (callId !== '' && entry.toolCallId !== '' && entry.toolCallId !== callId) continue;
        delete pendings.value[key];
        completed = true;
      }
      return completed;
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

  async function answer(approved: boolean, toolCallId?: string): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const current = toolCallId ? pendingOf(pending.value?.turnId ?? '', toolCallId) : pending.value;
    if (!current) {
      lastError.value = 'No pending confirmation.';
      return false;
    }
    const key = confirmKey(current.turnId, current.toolCallId);
    const stash = { ...current };
    // Approvals clear optimistically and roll back on failure; rejections
    // clear locally at once since the turn settles as rejected regardless.
    delete pendings.value[key];
    const reply = await channel.invoke('agent.answer', {
      turnId: current.turnId,
      toolCallId: current.toolCallId,
      approved,
      rejected: !approved,
    });
    if (!reply.ok) {
      lastError.value = reply.error?.msg || 'Answer failed.';
      if (approved) {
        pendings.value[key] = stash;
        approveRollbacks.value += 1;
      }
      return false;
    }
    lastError.value = '';
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
    pendings,
    pending,
    pendingList,
    pendingCount,
    lastError,
    approveRollbacks,
    confirmTimeouts,
    hasPending,
    bind,
    reset,
    pendingOf,
    remainingSec,
    tick,
    observe,
    enqueue,
    dequeue,
    answer,
    cancelTurn,
  };
});
