/** bigChange P2-4: smooth streaming text — 60ch/s, 0.5s backlog catch-up, >32KB passthrough, reduced-motion off. */
import { onUnmounted, ref, watch, type Ref } from 'vue';
import { SMOOTH_TEXT_LIMIT } from '@features/session/utils/renderContentLimits';

export const SMOOTH_CPS = 60;
export const SMOOTH_CATCHUP_S = 0.5;

export function useSmoothText(source: Ref<string>, opts?: { active?: Ref<boolean> }): Ref<string> {
  const shown = ref(source.value || '');
  const active = opts?.active;
  let raf = 0;
  let last = 0;
  let carry = 0;

  const reducedMotion = (): boolean => {
    try {
      return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
    } catch {
      return false;
    }
  };

  function flushAll(): void {
    shown.value = source.value || '';
    carry = 0;
  }

  function step(now: number): void {
    raf = 0;
    const full = source.value || '';
    if (shown.value === full) {
      last = now;
      return;
    }
    const activeOk = active ? active.value !== false : true;
    if (!activeOk || reducedMotion() || full.length > SMOOTH_TEXT_LIMIT) {
      flushAll();
      return;
    }
    const dt = Math.min((now - (last || now)) / 1000, 0.1);
    last = now;
    const backlog = full.length - shown.value.length;
    if (backlog <= 0) {
      shown.value = full;
      return;
    }
    const speed = backlog > SMOOTH_CPS * SMOOTH_CATCHUP_S ? backlog / SMOOTH_CATCHUP_S : SMOOTH_CPS;
    carry += speed * dt;
    const take = Math.min(backlog, Math.floor(carry));
    if (take > 0) {
      carry -= take;
      shown.value = full.slice(0, shown.value.length + take);
    }
    if (shown.value !== full) {
      raf = requestAnimationFrame(step);
    }
  }

  function kick(): void {
    const full = source.value || '';
    if (shown.value === full) return;
    const activeOk = active ? active.value !== false : true;
    if (!activeOk || reducedMotion() || full.length > SMOOTH_TEXT_LIMIT) {
      flushAll();
      return;
    }
    if (!raf) {
      last = performance.now();
      raf = requestAnimationFrame(step);
    }
  }

  watch(source, (next, prev) => {
    if (next === prev) return;
    if ((next || '').length < shown.value.length) {
      // Source shrank (reset/retry): snap.
      flushAll();
      return;
    }
    kick();
  });

  onUnmounted(() => {
    if (raf) cancelAnimationFrame(raf);
    raf = 0;
    // unmount 不丢字
    if (shown.value !== (source.value || '')) shown.value = source.value || '';
  });

  return shown;
}
