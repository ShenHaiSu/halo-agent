// src/Web.Client/src/components/common/fuzzy.ts
// 命令面板检索算法（05 §6.2）：三档匹配（相等/前缀/子串/顺序），无拼音。

export interface FuzzyScore {
  score: number;
  /** 命中的字符位置（用于高亮） */
  indices: number[];
}

function range(from: number, to: number): number[] {
  const out: number[] = [];
  for (let i = from; i < to; i++) out.push(i);
  return out;
}

export function fuzzyScore(text: string, query: string): FuzzyScore | null {
  if (!query) return { score: 1, indices: [] };
  const t = text.toLowerCase();
  const q = query.toLowerCase();

  // ① 完全相等
  if (t === q) return { score: 1000, indices: range(0, text.length) };

  // ② 前缀
  if (t.startsWith(q)) {
    return { score: 800 - Math.min(100, t.length - q.length), indices: range(0, q.length) };
  }

  // ③ 子串
  const idx = t.indexOf(q);
  if (idx >= 0) {
    return { score: 500 - Math.min(200, idx), indices: range(idx, idx + q.length) };
  }

  // ④ 顺序匹配（fuzzy）
  const indices: number[] = [];
  let ti = 0;
  let gaps = 0;
  for (const ch of q) {
    const found = t.indexOf(ch, ti);
    if (found < 0) return null;
    gaps += found - ti;
    indices.push(found);
    ti = found + 1;
  }
  if (indices.length === 0) return null;
  return { score: Math.max(1, 200 - gaps * 5 - (t.length - q.length)), indices };
}

export interface HighlightSegment {
  text: string;
  hit: boolean;
}

/** 渲染高亮片段 */
export function highlight(text: string, indices: number[]): HighlightSegment[] {
  if (indices.length === 0) return [{ text, hit: false }];
  const set = new Set(indices);
  const out: HighlightSegment[] = [];
  let buf = '';
  let curHit = set.has(0);
  for (let i = 0; i < text.length; i++) {
    const hit = set.has(i);
    if (hit !== curHit) {
      if (buf) out.push({ text: buf, hit: curHit });
      buf = '';
      curHit = hit;
    }
    buf += text[i];
  }
  if (buf) out.push({ text: buf, hit: curHit });
  return out;
}
/** 别名：与其它引用点的 `highlightSegments` 命名保持兼容 */
export const highlightSegments = highlight;
