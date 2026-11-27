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
 * tag 异常出现多条 final 时只保留到达序最后一条，其余降级进过程槽（内容不丢）；
 * 仅当 tag 完全缺失时才按 kind 兜底：最后一个非空 text 段当正文，其余当过程。
 * 结果 final 长度恒为 0 或 1，杜绝多 final 无分隔拼接造成的假稳态。
 */
export function splitHandoffSegs(segs: readonly TimelineLiveSeg[]): SplitHandoffSegs {
  const process: TimelineLiveSeg[] = [];
  const final: TimelineLiveSeg[] = [];
  const candidates = segs.filter((s) => s.tag === 'final');
  if (candidates.length > 0) {
    if (import.meta.env.DEV && candidates.length > 1) {
      console.warn('[handoff] multiple final segs; keeping last', {
        count: candidates.length,
        keys: candidates.map((s) => s.key),
      });
    }
    const chosen = candidates[candidates.length - 1];
    for (const s of segs) (s === chosen ? final : process).push(s);
    return { process, final };
  }
  // 有明确标签但没有 final：全按过程处理，绝不把过程正文误当正文种子。
  if (segs.some((s) => String(s.tag || '').length > 0)) {
    return { process: [...segs], final };
  }
  // 兜底：仅当 tag 全缺失时，取最后一个非空 text 段作为正文。
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
