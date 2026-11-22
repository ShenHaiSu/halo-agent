/** 交接期分组：把流段按落盘标签拆成「过程段 / 末轮正文」，并提供热降级开关。 */
import type { TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';

const DISABLE_KEY = 'halo-turn-handoff';

/**
 * 紧急降级开关：设置 localStorage 'halo-turn-handoff' = 'off' 时跳过交接容器，
 * Live 行走原来的 TimelineRow。仅用于线上热降级，不作为长期配置。
 */
export function isHandoffEnabled(): boolean {
  try {
    return String(window.localStorage.getItem(DISABLE_KEY) || '').toLowerCase() !== 'off';
  } catch {
    return true;
  }
}

export interface SplitHandoffSegs {
  process: TimelineLiveSeg[];
  final: TimelineLiveSeg[];
}

/**
 * 分组判据用 tag（真相源由落盘前的 finalizePending 写入），不用「数组最后一个 text seg」。
 * 仅当 tag 完全缺失时才按 kind 兜底：最后一个非空 text 段当正文，其余当过程。
 * 多轮迭代下 tag 可靠，兜底只为兼容尚未 finalize 的极端窗口。
 */
export function splitHandoffSegs(segs: readonly TimelineLiveSeg[]): SplitHandoffSegs {
  const process: TimelineLiveSeg[] = [];
  const final: TimelineLiveSeg[] = [];
  const tagged = segs.filter((s) => String(s.tag || '').length > 0);
  if (tagged.some((s) => s.tag === 'final')) {
    for (const s of segs) (s.tag === 'final' ? final : process).push(s);
    return { process, final };
  }
  // 兜底：无 final 标签时，取最后一个非空 text 段作为正文，避免把正文当过程收缩。
  let finalIndex = -1;
  for (let i = segs.length - 1; i >= 0; i--) {
    if (segs[i].kind === 'text' && String(segs[i].text || '').trim()) {
      finalIndex = i;
      break;
    }
  }
  for (let i = 0; i < segs.length; i++) {
    (i === finalIndex ? final : process).push(segs[i]);
  }
  return { process, final };
}
