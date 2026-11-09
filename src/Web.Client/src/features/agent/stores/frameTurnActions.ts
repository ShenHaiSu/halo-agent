import type { ContractEventFrame, ContractErrorCode } from '@/contract/agent';
import type { FrameTransport } from '@/transport/types';
import { describePromptFailure, readHint, type PromptFailureCopy } from './frameTurnFeed';
import type { TurnProjectionBoard } from './turnProjection';

export interface PromptInput {
  sessionId: string;
  text: string;
  needsConfirm?: boolean;
  providerId?: string;
  modelId?: string;
  timeoutSec?: number;
  attachments?: Array<Record<string, unknown>>;
}

export interface PromptAttachment {
  attId: string;
  fileName: string;
  mime: string;
  base64: string;
}

export interface ActionResult {
  ok: boolean;
  turnId?: string;
  code?: string;
  message?: string;
  retry?: boolean;
}

export interface CancelResult extends ActionResult {
  signaled?: boolean;
}

const SEEN_REQUEST_CAP = 500;

function newRequestId(): string {
  try {
    const cryptoRef = globalThis as unknown as { crypto?: { randomUUID?: () => string } };
    if (cryptoRef.crypto && typeof cryptoRef.crypto.randomUUID === 'function') {
      return cryptoRef.crypto.randomUUID();
    }
  } catch {
    /* fall through to the local fallback */
  }
  return `req-${Date.now().toString(36)}-${Math.floor(Math.random() * 1e6).toString(36)}`;
}

function cleanText(text: string): string {
  return (text || '').trim();
}

function failureOf(
  code: ContractErrorCode | string,
  message: string,
  hint: string,
  activeRequest: string
): PromptFailureCopy & { code: string } {
  const copy = describePromptFailure(code, { hint, activeRequest, message });
  return { code: String(code), message: copy.message, retry: copy.retry };
}

/**
 * Frame-lane turn actions. Mirrors the legacy send semantics (reentry
 * refusal, instant pending echo, request-key dedup, compacting and budget
 * gates) while speaking `agent.prompt` / `agent.retry` / `agent.cancel` /
 * `agent.answer` on the frame transport. The backend stays the turn owner;
 * this helper only guards the send edge and maps immediate failures.
 */
export class FrameTurnActions {
  private readonly seenRequests = new Map<string, number>();
  private readonly busyBySession = new Map<string, string>();
  private readonly compactingSessions = new Set<string>();
  private readonly budgetBlockedSessions = new Set<string>();
  private readonly lastPromptBySession = new Map<string, { text: string; attachments: PromptAttachment[] }>();
  private readonly pendingCancel = new Set<string>();
  private sendAttempts = 0;
  private reentryRefusals = 0;
  private compactRefusals = 0;
  private emptyRefusals = 0;

  constructor(private readonly transport: FrameTransport) {}

  stats(): { sendAttempts: number; reentryRefusals: number; compactRefusals: number; emptyRefusals: number } {
    return {
      sendAttempts: this.sendAttempts,
      reentryRefusals: this.reentryRefusals,
      compactRefusals: this.compactRefusals,
      emptyRefusals: this.emptyRefusals,
    };
  }

  /** Mark a session compacting; new turns are refused until it clears. */
  noteCompacting(sessionId: string, compacting: boolean): void {
    const id = (sessionId || '').trim();
    if (!id) return;
    if (compacting) this.compactingSessions.add(id);
    else this.compactingSessions.delete(id);
  }

  /** Mark a session budget-blocked; new turns are refused with a compress hint. */
  noteBudgetBlocked(sessionId: string, blocked: boolean): void {
    const id = (sessionId || '').trim();
    if (!id) return;
    if (blocked) this.budgetBlockedSessions.add(id);
    else this.budgetBlockedSessions.delete(id);
  }

  isBusy(sessionId: string): boolean {
    return this.busyBySession.has((sessionId || '').trim());
  }

  /** Clear the busy mark once a terminal event for the session arrives. */
  settle(sessionId: string): void {
    this.busyBySession.delete((sessionId || '').trim());
  }

  observeTerminal(frame: ContractEventFrame): boolean {
    if (!frame || frame.t !== 'evt') return false;
    if (frame.k !== 'turn.done' && frame.k !== 'turn.failed' && frame.k !== 'turn.cancelled') return false;
    const params = (frame.p ?? {}) as Record<string, unknown>;
    const sessionId = typeof params['sessionId'] === 'string' ? (params['sessionId'] as string) : '';
    if (sessionId.trim() === '') return false;
    this.settle(sessionId);
    return true;
  }

  private markSeen(requestId: string): void {
    this.seenRequests.set(requestId, Date.now());
    while (this.seenRequests.size > SEEN_REQUEST_CAP) {
      const oldest = this.seenRequests.keys().next();
      if (oldest.done) break;
      this.seenRequests.delete(oldest.value);
    }
  }

  async prompt(input: PromptInput): Promise<ActionResult> {
    const sessionId = (input.sessionId || '').trim();
    const text = cleanText(input.text);
    const attachments = Array.isArray(input.attachments) ? input.attachments : [];
    if (!sessionId || (text === '' && attachments.length === 0)) {
      this.emptyRefusals += 1;
      const copy = describePromptFailure('INVALID_PARAMS', {});
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    if (this.compactingSessions.has(sessionId)) {
      this.compactRefusals += 1;
      const copy = describePromptFailure('COMPACTING', {});
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    if (this.budgetBlockedSessions.has(sessionId)) {
      const copy = describePromptFailure('RATE_LIMITED', { hint: '预算超限，建议先压缩上下文' });
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const busy = this.busyBySession.get(sessionId);
    if (busy) {
      this.reentryRefusals += 1;
      const copy = describePromptFailure('TURN_IN_PROGRESS', { activeRequest: busy });
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const requestId = newRequestId();
    this.markSeen(requestId);
    this.busyBySession.set(sessionId, requestId);
    this.sendAttempts += 1;
    const payload: Record<string, unknown> = { sessionId, text, requestId };
    if (input.needsConfirm !== undefined) payload['needsConfirm'] = input.needsConfirm;
    if (input.providerId) payload['providerId'] = input.providerId;
    if (input.modelId) payload['modelId'] = input.modelId;
    if (typeof input.timeoutSec === 'number') payload['timeoutSec'] = input.timeoutSec;
    if (attachments.length > 0) payload['attachments'] = attachments;
    let reply;
    try {
      reply = await this.transport.invoke('agent.prompt', payload);
    } catch {
      this.busyBySession.delete(sessionId);
      return { ok: false, code: 'NETWORK_FAIL', message: describePromptFailure('NETWORK_FAIL', {}).message, retry: true };
    }
    if (!reply.ok) {
      const code = reply.error?.code ?? 'INTERNAL';
      if (code === 'TURN_IN_PROGRESS') {
        const copy = failureOf(code, reply.error?.msg ?? '', readHint(reply.error), this.busyBySession.get(sessionId) ?? requestId);
        return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
      }
      this.busyBySession.delete(sessionId);
      const copy = failureOf(code, reply.error?.msg ?? '', readHint(reply.error), requestId);
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const result = reply.result ?? {};
    const turnId = typeof result['turnId'] === 'string' ? (result['turnId'] as string) : '';
    this.lastPromptBySession.set(sessionId, { text, attachments: [] });
    return { ok: true, turnId };
  }

  async retry(sessionId: string): Promise<ActionResult> {
    const id = (sessionId || '').trim();
    if (!id) {
      const copy = describePromptFailure('INVALID_PARAMS', {});
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    if (this.compactingSessions.has(id)) {
      this.compactRefusals += 1;
      const copy = describePromptFailure('COMPACTING', {});
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const busy = this.busyBySession.get(id);
    if (busy) {
      this.reentryRefusals += 1;
      const copy = describePromptFailure('TURN_IN_PROGRESS', { activeRequest: busy });
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const requestId = newRequestId();
    this.markSeen(requestId);
    this.busyBySession.set(id, requestId);
    this.sendAttempts += 1;
    let reply;
    try {
      reply = await this.transport.invoke('agent.retry', { sessionId: id });
    } catch {
      this.busyBySession.delete(id);
      return { ok: false, code: 'NETWORK_FAIL', message: describePromptFailure('NETWORK_FAIL', {}).message, retry: true };
    }
    if (!reply.ok) {
      const code = reply.error?.code ?? 'INTERNAL';
      if (code !== 'TURN_IN_PROGRESS') this.busyBySession.delete(id);
      const copy = failureOf(code, reply.error?.msg ?? '', readHint(reply.error), requestId);
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const result = reply.result ?? {};
    const turnId = typeof result['turnId'] === 'string' ? (result['turnId'] as string) : '';
    return { ok: true, turnId };
  }

  async cancel(turnId: string): Promise<CancelResult> {
    const id = (turnId || '').trim();
    if (!id) {
      const copy = describePromptFailure('INVALID_PARAMS', {});
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry, signaled: false };
    }
    this.pendingCancel.add(id);
    let reply;
    try {
      reply = await this.transport.invoke('agent.cancel', { turnId: id });
    } catch {
      this.pendingCancel.delete(id);
      return { ok: false, code: 'NETWORK_FAIL', message: describePromptFailure('NETWORK_FAIL', {}).message, retry: true, signaled: false };
    }
    this.pendingCancel.delete(id);
    if (!reply.ok) {
      const code = reply.error?.code ?? 'INTERNAL';
      const copy = failureOf(code, reply.error?.msg ?? '', readHint(reply.error), id);
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry, signaled: false };
    }
    const result = reply.result ?? {};
    const signaled = result['signaled'];
    return { ok: true, turnId: id, signaled: typeof signaled === 'boolean' ? signaled : true };
  }

  async answer(turnId: string, approved: boolean, toolCallId?: string): Promise<ActionResult> {
    const id = (turnId || '').trim();
    if (!id) {
      const copy = describePromptFailure('INVALID_PARAMS', {});
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const payload: Record<string, unknown> = { turnId: id, approved, rejected: !approved };
    const callId = (toolCallId || '').trim();
    if (callId !== '') payload['toolCallId'] = callId;
    let reply;
    try {
      reply = await this.transport.invoke('agent.answer', payload);
    } catch {
      return { ok: false, code: 'NETWORK_FAIL', message: describePromptFailure('NETWORK_FAIL', {}).message, retry: true };
    }
    if (!reply.ok) {
      const code = reply.error?.code ?? 'INTERNAL';
      const copy = failureOf(code, reply.error?.msg ?? '', readHint(reply.error), id);
      return { ok: false, code: copy.code, message: copy.message, retry: copy.retry };
    }
    const result = reply.result ?? {};
    const turnIdOut = typeof result['turnId'] === 'string' ? (result['turnId'] as string) : id;
    return { ok: true, turnId: turnIdOut };
  }

  /**
   * Find the live turn of a session when the UI only knows the session id.
   * Used to fill `agent.cancel {turnId}` from the action dock.
   */
  activeTurnOf(sessionId: string, board: TurnProjectionBoard): string {
    const id = (sessionId || '').trim();
    if (!id) return '';
    for (const turn of board.turnsOfSession(id)) {
      if (turn.status === 'running' || turn.status === 'awaiting') return turn.turnId;
    }
    return '';
  }
}
