import type { ContractEventFrame, ContractErrorCode } from '@/contract/agent';
import { TurnProjectionBoard, type ProjectedTurn } from './turnProjection';
import { SMOOTH_CATCHUP_S, SMOOTH_CPS, SMOOTH_MAX_BACKLOG } from '@features/agent/composables/useSmoothText';

/**
 * Live-stream tuning shared with the legacy renderer. The frame lane feeds
 * text into the same smoothing hook, so both lanes advance at the same pace
 * and a side-by-side comparison shows no visual drift.
 */
export const TURN_STREAM_TUNING = {
  charsPerSecond: SMOOTH_CPS,
  catchupSeconds: SMOOTH_CATCHUP_S,
  maxBacklog: SMOOTH_MAX_BACKLOG,
} as const;

export interface PromptFailureCopy {
  code: string;
  message: string;
  retry: boolean;
}

function readHint(error: { hint?: string } | undefined): string {
  const hint = error?.hint;
  return typeof hint === 'string' ? hint : '';
}

/**
 * Immediate-failure copy for `agent.prompt` / `agent.retry` rejections.
 * Every branch mirrors the wording the legacy composer shows for the same
 * situation, including the in-progress request echo, so switching lanes
 * never changes what the user reads.
 */
export function describePromptFailure(
  code: ContractErrorCode | string,
  options: { hint?: string; activeRequest?: string; message?: string } = {}
): PromptFailureCopy {
  const hint = options.hint ?? '';
  const activeRequest = (options.activeRequest ?? '').trim() || 'unknown';
  switch (code) {
    case 'INVALID_PARAMS':
      return { code, message: '输入为空，请输入文字或添加图片后再发送', retry: false };
    case 'COMPACTING':
      return { code, message: '压缩中，新一轮请稍候', retry: false };
    case 'TURN_IN_PROGRESS':
      return { code, message: `上一轮仍在进行中（requestId=${activeRequest}），本发已忽略`, retry: false };
    case 'RATE_LIMITED':
      return { code, message: hint !== '' ? `限流中，请稍候重试（${hint}）` : '限流中，请稍候重试', retry: true };
    case 'PROVIDER_AUTH':
      return { code, message: '模型授权失败，请检查密钥与服务商配置', retry: false };
    case 'NETWORK_FAIL':
      return { code, message: '网络连接失败，请检查网络后重试', retry: true };
    case 'CANCELLED':
      return { code, message: '已停止生成。', retry: false };
    default:
      return { code, message: options.message ?? '启动推理失败', retry: false };
  }
}

/** Outcome copy for a terminal `turn.cancelled` event. */
export function describeCancel(signaled: boolean | null, reason: string): string {
  if (signaled === false) return '取消失败：后端未能中断本轮，请重试或新建会话';
  if (reason === 'rejected') return '已拒绝执行，轮次已取消';
  if (reason === 'timeout') return '确认超时，轮次已取消';
  return '已停止生成。';
}

/** Outcome copy for a terminal `turn.failed` event. */
export function describeTurnFailure(reason: string, hint: string): string {
  if (reason.trim() === '') return '推理失败（可点“重试本轮”）';
  return hint.trim() !== '' ? `${reason}：${hint}（可点“重试本轮”）` : `${reason}（可点“重试本轮”）`;
}

export interface TurnFeedStats {
  applied: number;
  dedupDrops: number;
  rejectedLegacyArgs: number;
  resyncs: number;
}

export interface TurnFeedSnapshot extends TurnFeedStats {
  /** High-water cursor: the newest sequence this feed has consumed. */
  waters: number;
  lastSeq: number;
}

const TURN_FACES = new Set(['turn.started', 'text.delta', 'text.done', 'turn.done', 'turn.failed', 'turn.cancelled', 'tool.confirm']);

function hasLegacyArgsKey(params: Record<string, unknown>): boolean {
  return Object.prototype.hasOwnProperty.call(params, 'argsDelta') || Object.prototype.hasOwnProperty.call(params, 'argsJson');
}

/**
 * Powered adapter between the frame pump and the turn projection board.
 * It owns a cursor copy for dedup (`seq <= waters` is dropped), appends
 * slices of the same turn, rejects stale incremental-args keys outright,
 * and records failure/cancel payloads for the outcome card. Rendering
 * itself stays in the timeline components; this feed only powers them.
 */
export class FrameTurnFeed {
  private readonly board = new TurnProjectionBoard();
  private waters = 0;
  private applied = 0;
  private dedupDrops = 0;
  private rejectedLegacyArgs = 0;
  private resyncs = 0;

  get lastSeq(): number {
    return this.waters;
  }

  stats(): TurnFeedSnapshot {
    return {
      applied: this.applied,
      dedupDrops: this.dedupDrops,
      rejectedLegacyArgs: this.rejectedLegacyArgs,
      resyncs: this.resyncs,
      waters: this.waters,
      lastSeq: this.waters,
    };
  }

  reset(): void {
    this.board.reset();
    this.waters = 0;
    this.applied = 0;
    this.dedupDrops = 0;
    this.rejectedLegacyArgs = 0;
    this.resyncs = 0;
  }

  /**
   * Rebuild after a journal gap: drop every projected card and continue
   * from the snapshot cursor instead of replaying stale slices.
   */
  noteResync(nextSeq: number): void {
    this.board.reset();
    this.waters = Math.max(0, Math.floor(nextSeq));
    this.resyncs += 1;
  }

  /**
   * Feed one event frame. Returns true when the frame powered the board.
   * Out-of-face events, cursor duplicates and frames carrying stale
   * incremental-args keys return false; the stale keys are additionally
   * counted so a backend regression is observable.
   */
  observe(frame: ContractEventFrame): boolean {
    if (!frame || frame.t !== 'evt') return false;
    if (typeof frame.seq !== 'number' || !TURN_FACES.has(frame.k)) return false;
    if (frame.seq <= this.waters) {
      this.dedupDrops += 1;
      return false;
    }
    const params = (frame.p ?? {}) as Record<string, unknown>;
    if ((frame.k === 'text.delta' || frame.k === 'tool.delta') && hasLegacyArgsKey(params)) {
      this.waters = frame.seq;
      this.rejectedLegacyArgs += 1;
      return false;
    }
    this.board.observe(frame);
    this.waters = frame.seq;
    this.applied += 1;
    return true;
  }

  turnOf(turnId: string): ProjectedTurn | undefined {
    return this.board.turnOf(turnId);
  }

  turnsOfSession(sessionId: string): ProjectedTurn[] {
    return this.board.turnsOfSession(sessionId);
  }

  /** Subscribe to a pump; any object with an `on` method works. */
  bindPump(pump: { on(kind: string, handler: (frame: ContractEventFrame) => void): () => void }): () => void {
    return pump.on('*', (frame) => {
      this.observe(frame);
    });
  }
}

export { readHint };
export type { ContractErrorCode };
