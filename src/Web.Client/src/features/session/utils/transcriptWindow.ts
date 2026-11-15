/** bigChange P2-1/P2-3: transcript window constants + grow/shrink helpers. */
export const INITIAL_MOUNT = 15;
export const WINDOW_MIN = 60;
export const STEP = 40;
export const TRANSCRIPT_PAGE_SIZE = 100;

export interface TranscriptWindow {
  start: number;
  end: number;
  total: number;
}

export function initialWindow(total: number): TranscriptWindow {
  const end = total;
  const start = Math.max(0, end - INITIAL_MOUNT);
  return { start, end, total };
}

export function fullWindow(total: number): TranscriptWindow {
  return { start: 0, end: total, total };
}

/** Grow visible window by STEP (top expansion); clamps at 0. Returns delta grown. */
export function growWindow(w: TranscriptWindow, step = STEP): { window: TranscriptWindow; grown: number } {
  const nextStart = Math.max(0, w.start - step);
  const grown = w.start - nextStart;
  return { window: { ...w, start: nextStart }, grown };
}

/** Expand to WINDOW_MIN after mount (second frame); only when pinned. */
export function expandToMin(w: TranscriptWindow): TranscriptWindow {
  if (w.end - w.start >= WINDOW_MIN) return w;
  return { ...w, start: Math.max(0, w.end - WINDOW_MIN) };
}

export function coversAllLoaded(w: TranscriptWindow, loadedCount: number): boolean {
  return w.start <= Math.max(0, w.total - loadedCount);
}

export function sliceWindow<T>(list: T[], w: TranscriptWindow): T[] {
  return list.slice(w.start, w.end);
}

/**
 * 「近顶」阈值：距顶小于该 px 即认为用户已到顶附近，应扩窗预取。
 *
 * 取值权衡：太小 → 用户能滚到 DOM 边缘才发现没内容（视觉上"撞墙"）；
 * 太大 → 一进入会话就白扩几轮窗。240px 约等于 2~3 条气泡，留出预取余量。
 */
export const NEAR_TOP_PX = 240;

/** 纯函数：是否已到达顶部预取区。负 scrollTop（Safari 橡皮筋）按 0 处理。 */
export function isNearTop(scrollTop: number, threshold = NEAR_TOP_PX): boolean {
  return Math.max(0, scrollTop) <= threshold;
}

// ---- need02-P4（p4-window）：按高度加权的窗口 ----
// P2 一行折叠后单条高度方差更大（单步直出 vs 多步折叠 vs 长文末段直出），按“条”切窗要么首屏空白要么超高。
// 权重 = 1 + steps（用户轮的 iteration 数；system/单条按 1）：
//  - 挂载预算 TURN_WINDOW_WEIGHT=32（≈15 个平均轮，与旧 INITIAL_MOUNT=15 turns 同量级）；
//  - 超高轮（单轮权重>预算）首屏仍挂该轮（只它一条，不截断轮内 user 头 + 汇总行语义）。
// 输入为轻量指纹（key/kind/steps），不依赖 UserTurn 对象，避免 utils 循环依赖。

export const TURN_WINDOW_WEIGHT = 32;

export interface TurnWeightLike {
  key: string;
  kind: string;
  steps?: number;
}

export function turnWeightOf(e: TurnWeightLike): number {
  if (e && e.kind === 'user-turn') return 1 + Math.max(0, Math.trunc(e.steps || 0));
  return 1;
}

/** 按权重预算从尾部向前收：返回应隐藏的前缀条数（hiddenAbove）。 */
export function hiddenAboveByWeight(entries: TurnWeightLike[], budget = TURN_WINDOW_WEIGHT): number {
  let acc = 0;
  let i = entries.length;
  while (i > 0) {
    const w = turnWeightOf(entries[i - 1] as TurnWeightLike);
    if (acc + w > budget && acc > 0) break;
    acc += w;
    i--;
  }
  return Math.max(0, i);
}
