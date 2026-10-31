// src/Web.Client/src/components/chat/MarkdownRenderer.ts

import { isSafeUrl } from './sanitize';

/* ============================================================================
 * 类型
 * ========================================================================== */

export type MdInline =
  | { type: 'text'; value: string }
  | { type: 'code'; value: string }
  | { type: 'strong'; children: MdInline[] }
  | { type: 'em'; children: MdInline[] }
  | { type: 'del'; children: MdInline[] }
   | { type: 'link'; href: string; title?: string; alt?: string; children: MdInline[]; isImage?: boolean }
  | { type: 'br' };

export type MdNode =
  | { type: 'paragraph'; children: MdInline[] }
  | { type: 'heading'; level: 1 | 2 | 3 | 4 | 5 | 6; children: MdInline[] }
  | { type: 'fence'; lang: string | null; value: string; closed: boolean }
  | { type: 'list'; ordered: boolean; start: number; loose: boolean; items: MdListItem[] }
  | { type: 'blockquote'; children: MdNode[] }
  | {
      type: 'table';
      align: Array<'left' | 'center' | 'right' | null>;
      head: MdInline[][];
      rows: MdInline[][][];
    }
  | { type: 'hr' };

export interface MdListItem {
  checked: boolean | null;
  children: MdNode[];
  /** 紧凑写法：单个 paragraph 时提取为 inline，避免多包一层 <p> */
  inline: MdInline[] | null;
}

export interface ParseOptions {
  /**
   * 宽容模式（流式渲染用）：
   *  - 未闭合围栏当普通段落
   *  - 表格分隔行缺失时不认表格
   *  - 列表项不完整时不抛错
   */
  lenient?: boolean;
  /** 最大嵌套深度（防栈溢出，默认 12） */
  maxDepth?: number;
  /** 单次解析的最大字符数（超出的部分截断并标记，默认 200 000） */
  maxChars?: number;
}

/* ============================================================================
 * 常量
 * ========================================================================== */

const DEFAULT_MAX_DEPTH = 12;
const DEFAULT_MAX_CHARS = 200_000;

const HR_RE = /^ {0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$/;
const FENCE_OPEN_RE = /^ {0,3}(`{3,}|~{3,})\s*([^\s`]*)\s*$/;
const HEADING_RE = /^ {0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$/;
const QUOTE_RE = /^ {0,3}>\s?(.*)$/;
const UL_RE = /^(\s*)([-*+])\s+(.*)$/;
const OL_RE = /^(\s*)(\d{1,9})[.)]\s+(.*)$/;
const TABLE_SEP_RE = /^ {0,3}\|?\s*:?-+\s*:?\s*(\|\s*:?-+\s*:?\s*)*\|?\s*$/;

/* ============================================================================
 * 入口
 * ========================================================================== */

/**
 * ============ 三个入口保证 ============
 *  ① 永不抛错（内部 try/catch，出错时退化为“整段当纯文本段落”）
 *  ② 永不返回 undefined（空输入返回 []）
 *  ③ 永不无限递归（maxDepth）
 * ====================================
 */
export function parseMarkdown(src: string, opts: ParseOptions = {}): MdNode[] {
  if (typeof src !== 'string' || src.length === 0) return [];

  const maxChars = opts.maxChars ?? DEFAULT_MAX_CHARS;
  const text = src.length > maxChars ? src.slice(0, maxChars) : src;

  try {
    const parser = new Parser(text, opts);
    const nodes = parser.parse();
    if (src.length > maxChars && nodes.length > 0) {
      nodes.push({
        type: 'paragraph',
        children: [{ type: 'text', value: '\n\n（内容超过 ' + maxChars + ' 字符，已截断显示）' }],
      });
    }
    return nodes;
  } catch (e) {
    // 兜底：整段当纯文本（保留换行）
    console.error('[MarkdownRenderer] 解析失败，退化为纯文本', e);
    return [
      {
        type: 'paragraph',
        children: [{ type: 'text', value: text.replace(/\r\n?/g, '\n') }],
      },
    ];
  }
}

/* ============================================================================
 * 块级解析
 * ========================================================================== */

class Parser {
  private lines: string[];
  private i = 0;
  private depth = 0;
  private readonly maxDepth: number;
  private readonly lenient: boolean;

  constructor(src: string, opts: ParseOptions) {
    // ① 去 BOM  ② 统一换行  ③ 制表符展开（Markdown 缩进规范要求）
    const normalized = src
      .replace(/^\uFEFF/, '')
      .replace(/\r\n?/g, '\n')
      .replace(/\t/g, '    ');
    this.lines = normalized.split('\n');
    this.maxDepth = opts.maxDepth ?? DEFAULT_MAX_DEPTH;
    this.lenient = opts.lenient === true;
  }

  parse(): MdNode[] {
    return this.parseBlocks();
  }

  private parseBlocks(): MdNode[] {
    if (this.depth >= this.maxDepth) {
      const rest = this.lines.slice(this.i).join('\n');
      this.i = this.lines.length;
      return rest.trim() ? [{ type: 'paragraph', children: [{ type: 'text', value: rest }] }] : [];
    }
    this.depth++;

    const out: MdNode[] = [];
    let guard = 0;
    const GUARD_MAX = this.lines.length * 4 + 100; // 防死循环

    while (this.i < this.lines.length && guard++ < GUARD_MAX) {
      const line = this.lines[this.i]!;

      if (line.trim() === '') {
        this.i++;
        continue;
      }

      const node =
        this.tryFence(line) ??
        this.tryHr(line) ??
        this.tryHeading(line) ??
        this.tryBlockquote() ??
        this.tryTable() ??
        this.tryList() ??
        this.tryParagraph();

      if (node) out.push(node);
      else this.i++; // 安全阀：理论上到不了这里
    }

    this.depth--;
    return out;
  }

  /* ---------- 围栏代码块 ---------- */

  private tryFence(line: string): MdNode | null {
    const m = FENCE_OPEN_RE.exec(line);
    if (!m) return null;

    const marker = m[1]!;
    const ch = marker[0]!;
    const langRaw = (m[2] ?? '').trim();
    const lang = langRaw ? langRaw.toLowerCase() : null;
    const openIdx = this.i;
    this.i++;

    const body: string[] = [];
    let closed = false;
    // 结束围栏：同字符、长度 >= 起始、后面只有空白
    const endRe = new RegExp(
      '^ {0,3}' + (ch === '`' ? '`' : '~') + '{' + marker.length + ',}\\s*$'
    );

    while (this.i < this.lines.length) {
      const cur = this.lines[this.i]!;
      if (endRe.test(cur)) {
        closed = true;
        this.i++;
        break;
      }
      body.push(cur);
      this.i++;
    }

    if (!closed && this.lenient) {
      // 宽容模式：未闭合 → 把这一行当段落起点（回退）
      this.i = openIdx;
      return null;
    }

    return { type: 'fence', lang, value: body.join('\n'), closed };
  }

  /* ---------- 分隔线 ---------- */

  private tryHr(line: string): MdNode | null {
    if (!HR_RE.test(line)) return null;
    this.i++;
    return { type: 'hr' };
  }

  /* ---------- 标题 ---------- */

  private tryHeading(line: string): MdNode | null {
    const m = HEADING_RE.exec(line);
    if (!m) return null;
    this.i++;
    const level = m[1]!.length as 1 | 2 | 3 | 4 | 5 | 6;
    return { type: 'heading', level, children: parseInline(m[2] ?? '') };
  }

  /* ---------- 引用 ---------- */

  private tryBlockquote(): MdNode | null {
    if (!QUOTE_RE.test(this.lines[this.i]!)) return null;

    const inner: string[] = [];
    while (this.i < this.lines.length) {
      const cur = this.lines[this.i]!;
      const m = QUOTE_RE.exec(cur);
      if (m) {
        inner.push(m[1] ?? '');
        this.i++;
        continue;
      }
      // 引用块内的空行（松散引用）
      if (cur.trim() === '' && QUOTE_RE.test(this.lines[this.i + 1] ?? '')) {
        inner.push('');
        this.i++;
        continue;
      }
      break;
    }

    const sub = new Parser(inner.join('\n'), {
      lenient: this.lenient,
      maxDepth: this.maxDepth - this.depth,
    });
    return { type: 'blockquote', children: sub.parse() };
  }

  /* ---------- 表格 ---------- */

  private tryTable(): MdNode | null {
    const headLine = this.lines[this.i]!;
    if (!headLine.includes('|')) return null;

    const sepLine = this.lines[this.i + 1];
    if (!sepLine || !sepLine.includes('-')) return null;
    if (!TABLE_SEP_RE.test(sepLine)) return null;

    // 列数必须与分隔行一致（否则不是表格）
    const headCells = splitRow(headLine);
    const sepCells = splitRow(sepLine);
    if (headCells.length !== sepCells.length) return null;
    if (headCells.length === 0) return null;

    const align = sepCells.map((c): 'left' | 'center' | 'right' | null => {
      const left = c.startsWith(':');
      const right = c.endsWith(':');
      if (left && right) return 'center';
      if (right) return 'right';
      if (left) return 'left';
      return null;
    });

    this.i += 2;

    const rows: MdInline[][][] = [];
    while (this.i < this.lines.length) {
      const cur = this.lines[this.i]!;
      if (cur.trim() === '' || !cur.includes('|')) break;
      // 遇到新的块起始就结束表格
      if (FENCE_OPEN_RE.test(cur) || HEADING_RE.test(cur) || QUOTE_RE.test(cur)) break;

      const cells = splitRow(cur);
      // 补齐 / 截断到表头列数
      while (cells.length < headCells.length) cells.push('');
      rows.push(cells.slice(0, headCells.length).map(parseInline));
      this.i++;
    }

    return {
      type: 'table',
      align,
      head: headCells.map(parseInline),
      rows,
    };
  }

  /* ---------- 列表 ---------- */

  private tryList(): MdNode | null {
    const firstLine = this.lines[this.i]!;
    const ul = UL_RE.exec(firstLine);
    const ol = OL_RE.exec(firstLine);
    if (!ul && !ol) return null;

    const ordered = ol !== null;
    const baseIndent = (ordered ? ol : ul)![1]!.length;
    const start = ordered ? parseInt(ol![2]!, 10) : 1;

    const items: MdListItem[] = [];
    let loose = false;
    let guard = 0;
    const GUARD_MAX = this.lines.length + 10;

    while (this.i < this.lines.length && guard++ < GUARD_MAX) {
      const line = this.lines[this.i]!;
      const m = ordered ? OL_RE.exec(line) : UL_RE.exec(line);
      if (!m) break;

      const indent = m[1]!.length;
      if (indent < baseIndent) break;
      if (indent > baseIndent + 3) break; // 更深的缩进属于子列表（由递归处理）

      const markerEnd = m[0]!.length;
      const contentIndent = markerEnd - (m[3] ?? '').length;

      // 收集本项所有行
      const itemLines: string[] = [m[3] ?? ''];
      this.i++;

      while (this.i < this.lines.length) {
        const cur = this.lines[this.i]!;

        // 空行：判断是否为本列表内的松散分隔
        if (cur.trim() === '') {
          const next = this.lines[this.i + 1];
          if (next === undefined) break;

          const nextSameList = ordered
            ? (() => {
                const nm = OL_RE.exec(next);
                return !!nm && nm[1]!.length === baseIndent;
              })()
            : (() => {
                const nm = UL_RE.exec(next);
                return !!nm && nm[1]!.length === baseIndent;
              })();

          // 空行后若缩进 >= contentIndent 说明是同一项的续块
          const nextIndent = /^\s*/.exec(next)?.[0].length ?? 0;
          const nextIsContinuation = nextIndent >= contentIndent && next.trim() !== '';

          if (!nextSameList && !nextIsContinuation) break;

          loose = true;
          itemLines.push('');
          this.i++;
          continue;
        }

        // 续行判断
        const curIndent = /^\s*/.exec(cur)?.[0].length ?? 0;

        // 同级别的新列表项 → 结束本项
        const sameLevel = ordered
          ? (() => {
              const nm = OL_RE.exec(cur);
              return !!nm && nm[1]!.length === baseIndent;
            })()
          : (() => {
              const nm = UL_RE.exec(cur);
              return !!nm && nm[1]!.length === baseIndent;
            })();
        if (sameLevel) break;

        // 缩进足够 → 续行（去掉 contentIndent 个空格）
        if (curIndent >= contentIndent && curIndent > baseIndent) {
          itemLines.push(trimIndent(cur, contentIndent));
          this.i++;
          continue;
        }

        // 缩进不足但非空（懒续行，CommonMark 允许）
        if (curIndent === 0 && !isBlockStart(cur)) {
          itemLines.push(cur);
          this.i++;
          continue;
        }

        break;
      }

      // 任务列表标记
      let checked: boolean | null = null;
      const task = /^\[([ xX])\]\s+(.*)$/.exec(itemLines[0] ?? '');
      if (task) {
        checked = task[1]!.toLowerCase() === 'x';
        itemLines[0] = task[2] ?? '';
      }

      const sub = new Parser(itemLines.join('\n'), {
        lenient: this.lenient,
        maxDepth: this.maxDepth - this.depth,
      });
      const children = sub.parse();

      const inline =
        children.length === 1 && children[0]!.type === 'paragraph' ? children[0]!.children : null;

      items.push({ checked, children, inline });
    }

    if (items.length === 0) return null;

    return { type: 'list', ordered, start, loose, items };
  }

  /* ---------- 段落 ---------- */

  private tryParagraph(): MdNode | null {
    const buf: string[] = [];

    while (this.i < this.lines.length) {
      const line = this.lines[this.i]!;
      if (line.trim() === '') break;
      if (buf.length > 0 && isBlockStart(line)) break;
      // 首行即使看起来像块起始也当段落（因为前面的 try* 都没匹配）
      buf.push(line);
      this.i++;
    }

    if (buf.length === 0) return null;
    return { type: 'paragraph', children: parseInline(buf.join('\n')) };
  }
}

/* ============================================================================
 * 块级工具
 * ========================================================================== */

function isBlockStart(line: string): boolean {
  return (
    FENCE_OPEN_RE.test(line) ||
    HEADING_RE.test(line) ||
    QUOTE_RE.test(line) ||
    UL_RE.test(line) ||
    OL_RE.test(line) ||
    HR_RE.test(line)
  );
}

/**
 * 去掉前 n 个空格（不足则尽量去）
 */
function trimIndent(line: string, n: number): string {
  let k = 0;
  while (k < n && k < line.length && line[k] === ' ') k++;
  return line.slice(k);
}

/**
 * 切分表格行。
 * 处理：前后竖线、`\|` 转义、行内代码里的竖线（`` `a|b` `` 不应切开）
 */
function splitRow(line: string): string[] {
  let s = line.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|') && !s.endsWith('\\|')) s = s.slice(0, -1);

  const cells: string[] = [];
  let buf = '';
  let inCode = false;
  let codeTicks = 0;

  for (let i = 0; i < s.length; i++) {
    const c = s[i]!;

    if (c === '\\' && s[i + 1] === '|') {
      buf += '|';
      i++;
      continue;
    }

    if (c === '`') {
      let n = 0;
      while (s[i + n] === '`') n++;
      if (!inCode) {
        inCode = true;
        codeTicks = n;
      } else if (n === codeTicks) {
        inCode = false;
        codeTicks = 0;
      }
      buf += '`'.repeat(n);
      i += n - 1;
      continue;
    }

    if (c === '|' && !inCode) {
      cells.push(buf);
      buf = '';
      continue;
    }

    buf += c;
  }
  cells.push(buf);

  return cells.map((c) => c.trim());
}

/* ============================================================================
 * 行内解析
 * ========================================================================== */

/**
 * ============ 顺序即正确性 ============
 *  1) 转义字符 `\*` 先占位（用私有区字符），最后再还原
 *  2) 行内代码 `` ` ``   —— 最高优先级，内部一切不解释
 *  3) 图片 / 链接
 *  4) 强调 *** → ** → *  /  __ → _  /  ~~
 *  5) 硬换行
 *  * 用占位符而不是“跳过区间”：因为强调的闭合符可能跨过代码区间，
 *    用占位可以保证“代码段整体视为一个不可分割的文本”。
 * =====================================
 */
const ESCAPE_MAP: Record<string, string> = {
  '\\': '\uE000',
  '`': '\uE001',
  '*': '\uE002',
  _: '\uE003',
  '~': '\uE004',
  '[': '\uE005',
  ']': '\uE006',
  '(': '\uE007',
  ')': '\uE008',
  '#': '\uE009',
  '|': '\uE00A',
  '>': '\uE00B',
  '-': '\uE00C',
  '+': '\uE00D',
  '.': '\uE00E',
  '!': '\uE00F',
};

const UNESCAPE_MAP: Record<string, string> = Object.fromEntries(
  Object.entries(ESCAPE_MAP).map(([k, v]) => [v, k])
);

function protectEscapes(src: string): string {
  return src.replace(
    /\\([\\`*_~[\]()#|>\-+.!])/g,
    (_m: string, ch: string) => ESCAPE_MAP[ch] ?? ch
  );
}

function restoreEscapes(s: string): string {
  return s.replace(/[\uE000-\uE00F]/g, (ch) => UNESCAPE_MAP[ch] ?? ch);
}

export function parseInline(raw: string): MdInline[] {
  if (!raw) return [];
  const src = protectEscapes(raw);
  const out: MdInline[] = [];
  let text = '';
  let i = 0;

  const flush = (): void => {
    if (text) {
      out.push({ type: 'text', value: restoreEscapes(text) });
      text = '';
    }
  };

  while (i < src.length) {
    const ch = src[i]!;

    /* ---------- 行内代码 ---------- */
    if (ch === '`') {
      let n = 0;
      while (src[i + n] === '`') n++;
      const ticks = '`'.repeat(n);
      const close = src.indexOf(ticks, i + n);
      if (close > i + n) {
        flush();
        // CommonMark：行内代码内容去掉首尾各一个空格（若首尾都是空格且不全为空格）
        let code = src.slice(i + n, close);
        if (/^ .* $/.test(code) && code.trim() !== '') code = code.slice(1, -1);
        out.push({ type: 'code', value: restoreEscapes(code) });
        i = close + n;
        continue;
      }
      // 未闭合：当普通字符
      text += ticks;
      i += n;
      continue;
    }

     if (ch === '!' && src[i + 1] === '[') {
       const m = /^!\[([^\]]*)\]\(\s*<?([^\s)>]+)>?(?:\s+"([^"]*)")?\s*\)/.exec(src.slice(i));
       if (m) {
         flush();
         const href = m[2]!;
         // P11-G3：alt 归 alt（无障碍），title 归还悬停；旧快照只有 title 时由渲染层回填 alt。
         const alt = restoreEscapes(m[1] ?? '') || undefined;
         const title = m[3] ? restoreEscapes(m[3]) : undefined;
         if (isSafeUrl(href, 'img')) {
           out.push({
             type: 'link',
             href,
             title,
             alt,
             children: alt ? [{ type: 'text', value: alt }] : [],
             isImage: true,
           });
         } else {
           // 不安全：只渲染 alt 文字
           if (alt) out.push({ type: 'text', value: alt });
         }
         i += m[0].length;
         continue;
       }
     }

    /* ---------- 链接 ---------- */
    if (ch === '[') {
      const m = /^\[([^\]]*)\]\(\s*<?([^\s)>]+)>?(?:\s+"([^"]*)")?\s*\)/.exec(src.slice(i));
      if (m) {
        flush();
        const href = m[2]!;
        const title = m[3] ? restoreEscapes(m[3]) : undefined;
        const inner = parseInline(m[1] ?? '');
        if (isSafeUrl(href, 'a')) {
          out.push({ type: 'link', href, title, children: inner });
        } else {
          // 不安全协议：只渲染文字
          out.push(...inner);
        }
        i += m[0].length;
        continue;
      }
    }

    /* ---------- 自动链接（裸 URL） ---------- */
    if (ch === 'h' && /^https?:\/\//i.test(src.slice(i))) {
      // 前面必须是空白或行首，且不在已处理的链接内
      const prev = out[out.length - 1];
      const prevIsText = prev?.type === 'text';
      const prevEndsSpace = !prevIsText || /[\s(（]$/.test((prev as { value: string }).value);
      if (prevEndsSpace || text === '') {
        const m = /^https?:\/\/[^\s<>"'）)]+/i.exec(src.slice(i));
        if (m) {
          flush();
          const url = m[0];
          out.push({ type: 'link', href: url, children: [{ type: 'text', value: url }] });
          i += url.length;
          continue;
        }
      }
    }

    /* ---------- 强调 ---------- */
    if (ch === '*' || ch === '_' || ch === '~') {
      // ***x***
      if (src.startsWith('***', i)) {
        const close = src.indexOf('***', i + 3);
        if (close > i + 3) {
          flush();
          out.push({
            type: 'strong',
            children: [
              { type: 'em', children: parseInline(restoreEscapes(src.slice(i + 3, close))) },
            ],
          });
          i = close + 3;
          continue;
        }
      }

      // **x** / __x__
      if (src.startsWith('**', i) || src.startsWith('__', i)) {
        const mark = src.slice(i, i + 2);
        const close = src.indexOf(mark, i + 2);
        if (close > i + 2) {
          flush();
          out.push({
            type: 'strong',
            children: parseInline(restoreEscapes(src.slice(i + 2, close))),
          });
          i = close + 2;
          continue;
        }
        // 未闭合：当普通字符（继续往下尝试单星号）
      }

      // ~~x~~
      if (src.startsWith('~~', i)) {
        const close = src.indexOf('~~', i + 2);
        if (close > i + 2) {
          flush();
          out.push({
            type: 'del',
            children: parseInline(restoreEscapes(src.slice(i + 2, close))),
          });
          i = close + 2;
          continue;
        }
      }

      // *x* / _x_
      if (ch === '*' || ch === '_') {
        // 下划线在词内不当强调（a_b_c）
        const prevCh = src[i - 1];
        const isWordInternal =
          ch === '_' && prevCh !== undefined && /[\w\u4e00-\u9fa5]/.test(prevCh);

        if (!isWordInternal) {
          const close = findEmClose(src, i + 1, ch);
          if (close > i + 1) {
            flush();
            out.push({
              type: 'em',
              children: parseInline(restoreEscapes(src.slice(i + 1, close))),
            });
            i = close + 1;
            continue;
          }
        }
      }
    }

    /* ---------- 硬换行 ---------- */
    if (ch === '\n') {
      flush();
      // 行尾两个空格 + 换行 = 硬换行；单换行也渲染为 <br>（GitHub 风格）
      out.push({ type: 'br' });
      i++;
      continue;
    }

    text += ch;
    i++;
  }

  flush();
  return mergeAdjacentText(out);
}

/**
 * 找斜体的闭合符。
 * 规则：闭合符后必须是空白、标点、或字符串结尾（避免 a*b*c 误判）。
 */
function findEmClose(src: string, from: number, mark: string): number {
  let k = from;
  while (k < src.length) {
    const idx = src.indexOf(mark, k);
    if (idx < 0) return -1;
    const next = src[idx + 1];
    if (next === undefined || /[\s.,!?;:、，。！？；：)）\]】]/.test(next)) {
      return idx;
    }
    k = idx + 1;
  }
  return -1;
}

function mergeAdjacentText(nodes: MdInline[]): MdInline[] {
  const out: MdInline[] = [];
  for (const n of nodes) {
    const prev = out[out.length - 1];
    if (prev && prev.type === 'text' && n.type === 'text') {
      prev.value += n.value;
      continue;
    }
    // 空的 text 节点丢弃
    if (n.type === 'text' && n.value === '') continue;
    out.push(n);
  }
  return out;
}

/* ============================================================================
 * 导出（供测试与外部使用）
  * ========================================================================== */
 export const __internal = {
   splitRow,
   parseInline: (s: string) => parseInline(s),
   trimIndent,
   isBlockStart,
   /** P11-G1：列表项 / 引用起始行（流式提交边界回溯用） */
   isListOrQuoteStart: (line: string): boolean =>
     UL_RE.test(line) || OL_RE.test(line) || QUOTE_RE.test(line),
 };
