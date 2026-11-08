/** settle veil — 3 stable frames / 600ms cap / class-fade leave (P03). */
export const STABLE_FRAMES = 3;
export const MAX_MS = 600;
/** @deprecated 废弃别名：唯一真相为 LEAVE_MS=180（= CSS --veil-leave-ms）。存量单测/旧 import 不炸，新代码一律用 LEAVE_MS。 */
export const FADE_MS = 160;
/** 退出淡出唯一真相（= CSS --veil-leave-ms: 180ms）。 */
export const LEAVE_MS = 180;

export type SettlePhase = 'covering' | 'leaving' | 'off';

export interface SettleSample {
  scrollHeight: number;
  clientHeight: number;
  /** P03-P2：未打点图片数；>0 时恒不判定 settled，但 600ms 超时仍兜底。 */
  imgPending?: number;
}

export function isSettled(samples: SettleSample[], stableFrames = STABLE_FRAMES): boolean {
  if (samples.length < stableFrames) return false;
  const tail = samples.slice(-stableFrames);
  if (tail.some((s) => (s.imgPending ?? 0) > 0)) return false;
  return tail.every((s) => s.scrollHeight === tail[0].scrollHeight && s.clientHeight === tail[0].clientHeight);
}

export interface SettleOptions {
  maxMs?: number;
  shortSession?: boolean;
  /** 退出淡出时长，默认 LEAVE_MS。 */
  leaveMs?: number;
  /** 最小遮盖时长（默认 120 = enter 时长，避免一闪即摘）；shortSession 分支无视此门。 */
  minCoverMs?: number;
  /** 是否允许 leaving 后仅一次回退，默认 true。 */
  allowRevert?: boolean;
}

export interface SettleController {
  phase: SettlePhase;
  sample(s: SettleSample): void;
  finish(): void;
  revertToCovering(): void;
  dispose(): void;
  veilCovering: boolean;
  veilLeaving: boolean;
  onPhase: ((p: SettlePhase) => void) | null;
}

export function createSettleController(opts?: SettleOptions): SettleController {
  const maxMs = opts?.maxMs ?? MAX_MS;
  const leaveMs = opts?.leaveMs ?? LEAVE_MS;
  const minCoverMs = opts?.minCoverMs ?? 0;
  const allowRevert = opts?.allowRevert ?? true;
  if (opts?.shortSession) {
    return {
      phase: 'off',
      veilCovering: false,
      veilLeaving: false,
      onPhase: null,
      sample() {},
      finish() {},
      revertToCovering() {},
      dispose() {},
    };
  }
  const samples: SettleSample[] = [];
  const startedAt = Date.now();
  let disposed = false;
  let reverted = false;
  let leaveTimer = 0;
  let maxTimer = 0;
  let minCoverTimer = 0;
  const ctrl: SettleController = {
    phase: 'covering',
    veilCovering: true,
    veilLeaving: false,
    onPhase: null,
    sample(s: SettleSample) {
      if (disposed) return;
      if (ctrl.phase !== 'covering') return;
      samples.push(s);
      if (samples.length > 12) samples.shift();
      const timedOut = Date.now() - startedAt >= maxMs;
      if (timedOut || isSettled(samples)) ctrl.finish();
    },
    finish() {
      if (disposed) return;
      if (ctrl.phase !== 'covering') return;
      const elapsed = Date.now() - startedAt;
      if (minCoverMs > 0 && elapsed < minCoverMs) {
        if (minCoverTimer) window.clearTimeout(minCoverTimer);
        minCoverTimer = window.setTimeout(() => {
          minCoverTimer = 0;
          ctrl.finish();
        }, minCoverMs - elapsed);
        return;
      }
      ctrl.phase = 'leaving';
      ctrl.veilLeaving = true;
      ctrl.onPhase?.('leaving');
      if (leaveTimer) window.clearTimeout(leaveTimer);
      leaveTimer = window.setTimeout(() => {
        leaveTimer = 0;
        if (disposed) return;
        ctrl.phase = 'off';
        ctrl.veilCovering = false;
        ctrl.veilLeaving = false;
        ctrl.onPhase?.('off');
      }, leaveMs);
    },
    revertToCovering() {
      if (disposed) return;
      if (!allowRevert || reverted) return;
      if (ctrl.phase !== 'leaving') return;
      if (leaveTimer) window.clearTimeout(leaveTimer);
      leaveTimer = 0;
      reverted = true;
      ctrl.phase = 'covering';
      ctrl.veilLeaving = false;
      ctrl.onPhase?.('covering');
    },
    dispose() {
      disposed = true;
      if (leaveTimer) window.clearTimeout(leaveTimer);
      if (maxTimer) window.clearTimeout(maxTimer);
      if (minCoverTimer) window.clearTimeout(minCoverTimer);
      leaveTimer = 0;
      maxTimer = 0;
      minCoverTimer = 0;
      ctrl.phase = 'off';
      ctrl.veilCovering = false;
      ctrl.veilLeaving = false;
      ctrl.onPhase = null;
    },
  };
  maxTimer = window.setTimeout(() => {
    maxTimer = 0;
    if (disposed) return;
    if (ctrl.phase === 'covering') ctrl.finish();
  }, maxMs);
  return ctrl;
}
