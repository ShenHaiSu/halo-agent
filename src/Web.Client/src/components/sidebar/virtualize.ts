// src/Web.Client/src/components/sidebar/virtualize.ts

/**
 * ============ 三种边界 ============
 *  ① 滚动到最顶端（scrollTop = 0）→ start = 0
 *  ② 滚动到底部（scrollTop = max）→ end = rows.length
 *  ③ 单行比视口还高 → start 与 end 都应是该行
 * ============ 为什么用 bytes 级累加而不是取整 ============
 *  混合行高（56 / 28）时，取整会让 offset 累积偏差（500 行后偏 10px+），
 *  导致最后几行位置偏移。所以 offsets 用精确整数（都是整数，无需浮点）。
 * ======================================================
 */
export interface RowLike {
  h: number;
}

export function buildOffsets(rows: RowLike[]): number[] {
  const out = new Array<number>(rows.length + 1);
  out[0] = 0;
  for (let i = 0; i < rows.length; i++) out[i + 1] = out[i]! + rows[i]!.h;
  return out;
}

/** 找出 y 所在的第一个行索引 */
export function findStartRow(offsets: number[], y: number): number {
  const n = offsets.length - 1; // 行数
  if (n <= 0) return 0;
  if (y <= 0) return 0;
  if (y >= offsets[n]!) return n - 1;

  let lo = 0;
  let hi = n - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    // offsets[mid+1] 是行 mid 的下边界
    if (offsets[mid + 1]! <= y) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

/** 计算可见区间 [start, end) */
export function computeVisible(
  offsets: number[],
  scrollTop: number,
  viewportH: number,
  buffer: number
): { start: number; end: number; padTop: number } {
  const n = offsets.length - 1;
  if (n <= 0) return { start: 0, end: 0, padTop: 0 };

  const first = findStartRow(offsets, scrollTop);
  const start = Math.max(0, first - buffer);

  const bottomY = scrollTop + viewportH;
  let last = first;
  while (last < n && offsets[last]! < bottomY) last++;
  const end = Math.min(n, last + buffer);

  return { start, end, padTop: offsets[start] ?? 0 };
}
