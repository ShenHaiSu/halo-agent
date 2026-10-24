/**
 * bigChange P2-4：流式文本平滑 —— 60ch/s 匀速、0.5s backlog 追赶、>32KB 直通、reduced-motion 直通。
 *
 * M2-1/§4.4 两个缺陷修复：
 *  A) backlog 无上限：上游一旦以快于 60ch/s 持续灌入（LLM 常见，一次 burst 500 字），
 *     shown 会永远落后 → 观感「越流越慢」。现超过 SMOOTH_MAX_BACKLOG 直接快进到
 *     「全文 - MAX_BACKLOG」，剩余部分继续平滑。
 *  B) 未绑定段 key：尾段毕业/切换时 source 会从「段 A 全文」突变成「段 B 的空串/前几个字」，
 *     仅靠「变短则 snap」会闪一下完整文本。现由 opts.key 监听段变化 → 整体重置。
 */
import { onUnmounted, ref, watch, type Ref } from 'vue';
import { SMOOTH_TEXT_LIMIT } from '@features/session/utils/renderContentLimits';

export const SMOOTH_CPS = 60;
export const SMOOTH_CATCHUP_S = 0.5;
/** 允许落后的最大字符数；超出即快进（防止「越流越慢」）。 */
export const SMOOTH_MAX_BACKLOG = 400;

/**
 * 单帧推进计算（纯函数，便于单测；不依赖 rAF / performance）。
 * @param shownLen 当前已显示字符数
 * @param fullLen  目标全文长度
 * @param dtSec    距上一帧的秒数
 * @param carry    上一帧未消化的字符余量
 * @returns take = 本帧应追加的字符数，carry = 新的余量
 */
export function computeSmoothStep(shownLen: number, fullLen: number, dtSec: number, carry: number): { take: number; carry: number } {
  const backlog = fullLen - shownLen;
  if (backlog <= 0) return { take: 0, carry: 0 };
  const speed = backlog > SMOOTH_CPS * SMOOTH_CATCHUP_S ? backlog / SMOOTH_CATCHUP_S : SMOOTH_CPS;
  const carried = carry + speed * Math.max(0, dtSec);
  const take = Math.min(backlog, Math.floor(carried));
  return { take, carry: carried - take };
}

export function useSmoothText(source: Ref<string>, opts?: { active?: Ref<boolean>; key?: Ref<string> }): Ref<string> {
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
    if (backlog > SMOOTH_MAX_BACKLOG) {
      // 落后太多：快进到「全文 - MAX_BACKLOG」，剩余部分继续平滑，避免永远追不上。
      shown.value = full.slice(0, full.length - SMOOTH_MAX_BACKLOG);
      carry = 0;
      raf = requestAnimationFrame(step);
      return;
    }
    const r = computeSmoothStep(shown.value.length, full.length, dt, carry);
    carry = r.carry;
    if (r.take > 0) shown.value = full.slice(0, shown.value.length + r.take);
    if (shown.value !== full) raf = requestAnimationFrame(step);
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

  // 缺陷 B 修复：段切换（尾段毕业 / 新一轮开段）时整体重置，而不是 snap 后闪一下完整文本。
  if (opts?.key) {
    watch(opts.key, () => {
      if (raf) cancelAnimationFrame(raf);
      raf = 0;
      carry = 0;
      shown.value = source.value || '';
    });
  }

  onUnmounted(() => {
    if (raf) cancelAnimationFrame(raf);
    raf = 0;
    // unmount 不丢字
    if (shown.value !== (source.value || '')) shown.value = source.value || '';
  });

  return shown;
}
