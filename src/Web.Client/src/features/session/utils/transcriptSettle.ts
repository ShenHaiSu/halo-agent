/** bigChange P2-1/P2-4: settle veil — 3 stable frames / 600ms cap / 160ms fade + skeleton. */
export const STABLE_FRAMES = 3;
export const MAX_MS = 600;
export const FADE_MS = 160;

export type SettlePhase = 'covering' | 'leaving' | 'off';

export interface SettleSample {
  scrollHeight: number;
  clientHeight: number;
}

export function isSettled(samples: SettleSample[], stableFrames = STABLE_FRAMES): boolean {
  if (samples.length < stableFrames) return false;
  const tail = samples.slice(-stableFrames);
  return tail.every((s) => s.scrollHeight === tail[0].scrollHeight && s.clientHeight === tail[0].clientHeight);
}

export interface SettleController {
  phase: SettlePhase;
  sample(s: SettleSample): void;
  finish(): void;
  dispose(): void;
  veilCovering: boolean;
  onPhase: ((p: SettlePhase) => void) | null;
}

export function createSettleController(opts?: { maxMs?: number; shortSession?: boolean }): SettleController {
  const maxMs = opts?.maxMs ?? MAX_MS;
  if (opts?.shortSession) {
    return {
      phase: 'off',
      veilCovering: false,
      onPhase: null,
      sample() {},
      finish() {},
      dispose() {},
    };
  }
  const samples: SettleSample[] = [];
  const startedAt = Date.now();
  const ctrl: SettleController = {
    phase: 'covering',
    veilCovering: true,
    onPhase: null,
    sample(s: SettleSample) {
      if (ctrl.phase !== 'covering') return;
      samples.push(s);
      if (samples.length > 12) samples.shift();
      const timedOut = Date.now() - startedAt >= maxMs;
      if (timedOut || isSettled(samples)) ctrl.finish();
    },
    finish() {
      if (ctrl.phase !== 'covering') return;
      ctrl.phase = 'leaving';
      ctrl.onPhase?.('leaving');
      window.setTimeout(() => {
        ctrl.phase = 'off';
        ctrl.veilCovering = false;
        ctrl.onPhase?.('off');
      }, FADE_MS);
    },
    dispose() {
      ctrl.phase = 'off';
      ctrl.veilCovering = false;
    },
  };
  window.setTimeout(() => {
    if (ctrl.phase === 'covering') ctrl.finish();
  }, maxMs);
  return ctrl;
}
