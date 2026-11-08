import type { ContractEventFrame } from '../../../contract/agent';

export type ProjectedTurnStatus = 'running' | 'awaiting' | 'done' | 'failed' | 'cancelled';

export interface ProjectedToolCall {
  toolCallId: string;
  toolName: string;
  argsText: string;
  ok: boolean | null;
}

export interface ProjectedTurn {
  turnId: string;
  sessionId: string;
  text: string;
  /** Streamed reasoning channel; rendered in the timeline reasoning slot. */
  reasoning: string;
  status: ProjectedTurnStatus;
  /** Failure record from turn.failed; shown in the outcome card. */
  reason: string;
  hint: string;
  /** Cancel record from turn.cancelled; null until the event arrives. */
  signaled: boolean | null;
  cancelReason: string;
  /** Section number from the history index; null when the event omits it. */
  sectionIndex: number | null;
  tools: ProjectedToolCall[];
  updatedSeq: number;
}

function readString(record: Record<string, unknown>, key: string): string {
  const value = record[key];
  return typeof value === 'string' ? value : '';
}

function readSectionIndex(record: Record<string, unknown>): number | null {
  const candidates = [record['historyIndex'], record['index'], record['sectionIndex']];
  for (const candidate of candidates) {
    if (typeof candidate === 'number' && Number.isFinite(candidate) && candidate >= 0) {
      return Math.floor(candidate);
    }
  }
  return null;
}

/**
 * Render-only projection of engine turn frames. The backend owns the turn
 * state machine; this board folds `turn.*` / `text.*` / `tool.*` frames
 * into cards the timeline components already know how to render. It keeps
 * no truth: a snapshot plus a fresh poll rebuilds every entry, and unknown
 * frames are ignored so future event additions stay compatible.
 */
export class TurnProjectionBoard {
  private readonly turns = new Map<string, ProjectedTurn>();
  private readonly bySession = new Map<string, string[]>();

  reset(): void {
    this.turns.clear();
    this.bySession.clear();
  }

  turnOf(turnId: string): ProjectedTurn | undefined {
    return this.turns.get(turnId);
  }

  turnsOfSession(sessionId: string): ProjectedTurn[] {
    const ids = this.bySession.get(sessionId) ?? [];
    const cards: ProjectedTurn[] = [];
    for (const id of ids) {
      const card = this.turns.get(id);
      if (card) cards.push({ ...card, tools: [...card.tools] });
    }
    return cards;
  }

  observe(frame: ContractEventFrame): void {
    if (!frame || frame.t !== 'evt') return;
    const params = (frame.p ?? {}) as Record<string, unknown>;
    switch (frame.k) {
      case 'turn.started':
        this.startTurn(params, frame.seq);
        break;
      case 'text.delta':
        this.appendText(params, frame.seq);
        break;
      case 'tool.start':
        this.startTool(params, frame.seq);
        break;
      case 'tool.delta':
        this.appendToolArgs(params, frame.seq);
        break;
      case 'tool.end':
        this.finishTool(params, frame.seq);
        break;
      case 'tool.confirm':
        this.markAwaiting(params, frame.seq);
        break;
      case 'turn.done':
        this.finishTurn(params, frame.seq, 'done');
        break;
      case 'turn.failed':
        this.finishTurn(params, frame.seq, 'failed');
        break;
      case 'turn.cancelled':
        this.finishTurn(params, frame.seq, 'cancelled');
        break;
      default:
        break;
    }
  }

  private ensure(turnId: string, sessionId: string, seq: number): ProjectedTurn {
    let card = this.turns.get(turnId);
    if (!card) {
      card = {
        turnId,
        sessionId,
        text: '',
        reasoning: '',
        status: 'running',
        reason: '',
        hint: '',
        signaled: null,
        cancelReason: '',
        sectionIndex: null,
        tools: [],
        updatedSeq: seq,
      };
      this.turns.set(turnId, card);
      if (sessionId !== '') {
        const ids = this.bySession.get(sessionId) ?? [];
        if (!ids.includes(turnId)) {
          ids.push(turnId);
          this.bySession.set(sessionId, ids);
        }
      }
    }
    if (sessionId !== '' && card.sessionId === '') {
      card.sessionId = sessionId;
      const ids = this.bySession.get(sessionId) ?? [];
      if (!ids.includes(turnId)) {
        ids.push(turnId);
        this.bySession.set(sessionId, ids);
      }
    }
    return card;
  }

  private startTurn(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    if (turnId === '') return;
    const card = this.ensure(turnId, readString(params, 'sessionId'), seq);
    card.status = 'running';
    const section = readSectionIndex(params);
    if (section !== null) card.sectionIndex = section;
    card.updatedSeq = seq;
  }

  private appendText(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    if (turnId === '') return;
    const card = this.ensure(turnId, readString(params, 'sessionId'), seq);
    if (readString(params, 'kind') === 'reasoning') {
      card.reasoning += readString(params, 'delta');
    } else {
      card.text += readString(params, 'delta');
    }
    card.updatedSeq = seq;
  }

  private startTool(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    const toolCallId = readString(params, 'toolCallId');
    if (turnId === '' || toolCallId === '') return;
    const card = this.ensure(turnId, readString(params, 'sessionId'), seq);
    if (!card.tools.some((tool) => tool.toolCallId === toolCallId)) {
      card.tools.push({
        toolCallId,
        toolName: readString(params, 'toolName'),
        argsText: '',
        ok: null,
      });
    }
    card.updatedSeq = seq;
  }

  private appendToolArgs(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    const toolCallId = readString(params, 'toolCallId');
    if (turnId === '' || toolCallId === '') return;
    const card = this.ensure(turnId, readString(params, 'sessionId'), seq);
    const tool = card.tools.find((entry) => entry.toolCallId === toolCallId);
    if (!tool) {
      card.tools.push({
        toolCallId,
        toolName: readString(params, 'toolName'),
        argsText: readString(params, 'delta') || readString(params, 'argsDelta') || readString(params, 'argsJson'),
        ok: null,
      });
    } else {
      tool.argsText += readString(params, 'delta') || readString(params, 'argsDelta') || readString(params, 'argsJson');
    }
    card.updatedSeq = seq;
  }

  private finishTool(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    if (turnId === '') return;
    const card = this.turns.get(turnId);
    if (!card) return;
    const toolCallId = readString(params, 'toolCallId');
    const ok = params['ok'];
    const resolved = typeof ok === 'boolean' ? ok : true;
    if (toolCallId === '') {
      const last = card.tools[card.tools.length - 1];
      if (last && last.ok === null) last.ok = resolved;
    } else {
      const tool = card.tools.find((entry) => entry.toolCallId === toolCallId);
      if (tool) tool.ok = resolved;
    }
    card.updatedSeq = seq;
  }

  private markAwaiting(params: Record<string, unknown>, seq: number): void {
    const turnId = readString(params, 'turnId');
    if (turnId === '') return;
    const card = this.ensure(turnId, readString(params, 'sessionId'), seq);
    card.status = 'awaiting';
    card.updatedSeq = seq;
  }

  private finishTurn(params: Record<string, unknown>, seq: number, status: ProjectedTurnStatus): void {
    const turnId = readString(params, 'turnId');
    if (turnId === '') return;
    const card = this.ensure(turnId, readString(params, 'sessionId'), seq);
    card.status = status;
    if (status === 'failed') {
      card.reason = readString(params, 'reason') || readString(params, 'code');
      card.hint = readString(params, 'hint');
    }
    if (status === 'cancelled') {
      const signaled = params['signaled'];
      card.signaled = typeof signaled === 'boolean' ? signaled : null;
      card.cancelReason = readString(params, 'reason');
    }
    card.updatedSeq = seq;
  }
}
