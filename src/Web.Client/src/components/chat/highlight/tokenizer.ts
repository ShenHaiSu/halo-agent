// src/Web.Client/src/components/chat/highlight/tokenizer.ts
// P5 D5-2 三色高亮：单遍扫描 + sticky 正则。
export type TokenType = 'kw' | 'str' | 'com' | 'num' | 'punct' | 'plain';

export interface Token {
  type: TokenType;
  value: string;
}

export interface LangDef {
  name: string;
  /** 注释（按优先级，长的在前） */
  comments: RegExp[];
  /** 字符串（按优先级，长的在前） */
  strings: RegExp[];
  /** 关键字（用 ^...$ 包住，对单个词测试） */
  keywords: RegExp;
  /** 数字（用 ^...$ 包住） */
  numbers: RegExp;
}

/**
 * 单遍扫描 + sticky 正则。
 * 不用 replace 做多轮替换：会把字符串里的关键字也高亮。
 * sticky（y）：只在 lastIndex 处匹配，不从别处搜索 → O(n)。
 */
export function tokenize(src: string, lang: LangDef): Token[] {
  const out: Token[] = [];
  const n = src.length;
  let i = 0;

  const matchers: Array<{ re: RegExp; type: TokenType }> = [
    ...lang.comments.map((re) => ({ re, type: 'com' as TokenType })),
    ...lang.strings.map((re) => ({ re, type: 'str' as TokenType })),
  ];
  // 合并快查：把同一语言的注释/字符串正则按原优先级拼成一个 sticky 正则。
  // JS 交替分支按书写顺序尝试，与逐个 exec 的优先级一致；命中后再用原正则
  // 做一次全长归类，保证 Token 类型不变。非命中位置从"每次 7 次 exec"
  // 降为"每次 1 次"，仍是单遍扫描 + sticky，复杂度仍为 O(n)。
  const master = getMasterMatcher(lang);

  while (i < n) {
    // ① 注释 / 字符串（必须整段吞掉，最高优先级）
    let hit = false;
    master.lastIndex = i;
    const mr = master.exec(src);
    if (mr && mr.index === i && mr[0].length > 0) {
      for (const m of matchers) {
        m.re.lastIndex = i;
        const r = m.re.exec(src);
        if (r && r.index === i && r[0].length === mr[0].length) {
          out.push({ type: m.type, value: r[0] });
          i += r[0].length;
          hit = true;
          break;
        }
      }
    }
    if (hit) continue;

    const ch = src[i]!;

    // ② 空白（ASCII 快查 + 非 ASCII 回退正则）
    {
      let j = i;
      while (j < n) {
        const c = src.charCodeAt(j);
        if (c === 32 || c === 9 || c === 10 || c === 13 || c === 11 || c === 12) j++;
        else if (c < 128) break;
        else if (/\s/.test(src[j]!)) j++;
        else break;
      }
      if (j > i) {
        out.push({ type: 'plain', value: src.slice(i, j) });
        i = j;
        continue;
      }
    }

    // ③ 数字开头
    if (ch >= '0' && ch <= '9') {
      let j = i;
      while (j < n) {
        const c = src.charCodeAt(j);
        const isNum =
          (c >= 48 && c <= 57) || // 0-9
          (c >= 65 && c <= 70) || // A-F
          (c >= 97 && c <= 102) || // a-f
          c === 46 ||
          c === 95 || // . _
          c === 120 ||
          c === 88 || // x X
          c === 111 ||
          c === 79 || // o O
          c === 98 ||
          c === 66 || // b B
          c === 101 ||
          c === 69 || // e E
          c === 43 ||
          c === 45; // + -
        if (!isNum) break;
        // 只在 e/E 后允许 +/-，避免把 "1-2" 当成一个数字
        if ((c === 43 || c === 45) && !/[eE]/.test(src[j - 1] ?? '')) break;
        j++;
      }
      out.push({ type: 'num', value: src.slice(i, j) });
      i = j;
      continue;
    }

    // ④ 标识符
    {
      const c0 = src.charCodeAt(i);
      const isWordStart =
        (c0 >= 48 && c0 <= 57) ||
        (c0 >= 65 && c0 <= 90) ||
        (c0 >= 97 && c0 <= 122) ||
        c0 === 95 ||
        c0 === 36 ||
        (c0 >= 0x4e00 && c0 <= 0x9fa5);
      if (isWordStart) {
        let j = i + 1;
        while (j < n) {
          const c = src.charCodeAt(j);
          if (
            (c >= 48 && c <= 57) ||
            (c >= 65 && c <= 90) ||
            (c >= 97 && c <= 122) ||
            c === 95 ||
            c === 36 ||
            (c >= 0x4e00 && c <= 0x9fa5)
          )
            j++;
          else break;
        }
        const word = src.slice(i, j);
        lang.keywords.lastIndex = 0;
        lang.numbers.lastIndex = 0;
        if (lang.keywords.test(word)) out.push({ type: 'kw', value: word });
        else if (lang.numbers.test(word)) out.push({ type: 'num', value: word });
        else out.push({ type: 'plain', value: word });
        i = j;
        continue;
      }
    }

    // ⑤ 标点
    out.push({ type: 'punct', value: ch });
    i++;
  }

  return mergeSame(out);
}

function mergeSame(tokens: Token[]): Token[] {
  const out: Token[] = [];
  for (const t of tokens) {
    const prev = out[out.length - 1];
    if (prev && prev.type === t.type) prev.value += t.value;
    else out.push({ ...t });
  }
  return out;
}

// 合并正则缓存：同一 LangDef 只拼一次（sources 不变，正则可复用）。
// 注意复用时每次都要设 lastIndex（sticky），调用方已处理。
const masterCache = new WeakMap<LangDef, RegExp>();

function getMasterMatcher(lang: LangDef): RegExp {
  let re = masterCache.get(lang);
  if (!re) {
    const parts: string[] = [];
    for (const r of lang.comments) parts.push(`(?:${r.source})`);
    for (const r of lang.strings) parts.push(`(?:${r.source})`);
    re = new RegExp(`(?:${parts.join('|')})`, 'y');
    masterCache.set(lang, re);
  }
  return re;
}
