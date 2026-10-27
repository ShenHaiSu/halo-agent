// src/Web.Client/src/utils/diff.ts

/**
 * ============ 极简行级 diff（LCS） ============
 *  标准 LCS 动态规划回溯，输出 same/add/del 三类行。
 *  限制：任一侧超过 2000 行时截断到 2000 行（工具卡片预览场景下足够）；
 *  矩阵过大（> 400 万格）时退化为“公共前后缀”快算，保证不卡死。
 * ===============================================
 */
export type DiffLineType = 'same' | 'add' | 'del';

export interface DiffLine {
  type: DiffLineType;
  text: string;
}

const MAX_DIFF_LINES = 2000;
/** LCS 矩阵上限（格数），超出则用前后缀快算 */
const MAX_CELLS = 4_000_000;

export function diffLines(a: string, b: string): DiffLine[] {
  const as = (a === '' ? [] : a.split('\n')).slice(0, MAX_DIFF_LINES);
  const bs = (b === '' ? [] : b.split('\n')).slice(0, MAX_DIFF_LINES);
  const n = as.length;
  const m = bs.length;

  if (n === 0) return bs.map((text) => ({ type: 'add' as const, text }));
  if (m === 0) return as.map((text) => ({ type: 'del' as const, text }));

  if ((n + 1) * (m + 1) > MAX_CELLS) return prefixSuffix(as, bs);

  // dp[i * w + j] = LCS(as[0..i), bs[0..j)) 的长度
  const w = m + 1;
  const dp = new Uint32Array((n + 1) * w);
  for (let i = 1; i <= n; i++) {
    const ai = as[i - 1];
    for (let j = 1; j <= m; j++) {
      dp[i * w + j] =
        ai === bs[j - 1]
          ? (dp[(i - 1) * w + (j - 1)] ?? 0) + 1
          : Math.max(dp[(i - 1) * w + j] ?? 0, dp[i * w + (j - 1)] ?? 0);
    }
  }

  const out: DiffLine[] = [];
  let i = n;
  let j = m;
  while (i > 0 && j > 0) {
    if (as[i - 1] === bs[j - 1]) {
      out.push({ type: 'same', text: as[i - 1]! });
      i--;
      j--;
    } else if ((dp[(i - 1) * w + j] ?? 0) >= (dp[i * w + (j - 1)] ?? 0)) {
      out.push({ type: 'del', text: as[i - 1]! });
      i--;
    } else {
      out.push({ type: 'add', text: bs[j - 1]! });
      j--;
    }
  }
  while (i > 0) {
    out.push({ type: 'del', text: as[i - 1]! });
    i--;
  }
  while (j > 0) {
    out.push({ type: 'add', text: bs[j - 1]! });
    j--;
  }
  out.reverse();
  return out;
}

/** 超大输入的快算：公共前缀 + 公共后缀 + 中间整体删/增 */
function prefixSuffix(as: string[], bs: string[]): DiffLine[] {
  let pre = 0;
  while (pre < as.length && pre < bs.length && as[pre] === bs[pre]) pre++;

  let sufA = as.length - 1;
  let sufB = bs.length - 1;
  while (sufA >= pre && sufB >= pre && as[sufA] === bs[sufB]) {
    sufA--;
    sufB--;
  }

  const out: DiffLine[] = [];
  for (let k = 0; k < pre; k++) out.push({ type: 'same', text: as[k]! });
  for (let k = pre; k <= sufA; k++) out.push({ type: 'del', text: as[k]! });
  for (let k = pre; k <= sufB; k++) out.push({ type: 'add', text: bs[k]! });
  for (let k = sufA + 1; k < as.length; k++) out.push({ type: 'same', text: as[k]! });
  return out;
}
