// src/Web.Client/src/utils/ansi.ts

/**
 * ============ 为什么必须处理 ANSI ============
 *  shell 输出（尤其是 PowerShell 与 git）会带 ESC[32m 这样的颜色码。
 *  不处理的话用户看到一堆 “[32m” 乱码。
 * ============ 支持范围 ============
 *  前景 30-37 / 90-97，背景 40-47，加粗 1，重置 0。
 *  渲染层把 classes 映射到样式（.ansi-fg-red 等），本文件只做解析。
 * ===============================================
 */
export interface AnsiSegment {
  text: string;
  classes: string[];
}

/** 标准前景 30-37 → 语义类名 */
const FG_STD: Record<number, string> = {
  30: 'ansi-fg-black',
  31: 'ansi-fg-red',
  32: 'ansi-fg-green',
  33: 'ansi-fg-yellow',
  34: 'ansi-fg-blue',
  35: 'ansi-fg-magenta',
  36: 'ansi-fg-cyan',
  37: 'ansi-fg-white',
};

/** 高亮前景 90-97 → 语义类名 */
const FG_BRIGHT: Record<number, string> = {
  90: 'ansi-fg-bright-black',
  91: 'ansi-fg-bright-red',
  92: 'ansi-fg-bright-green',
  93: 'ansi-fg-bright-yellow',
  94: 'ansi-fg-bright-blue',
  95: 'ansi-fg-bright-magenta',
  96: 'ansi-fg-bright-cyan',
  97: 'ansi-fg-bright-white',
};

/** 背景 40-47 → 语义类名 */
const BG_STD: Record<number, string> = {
  40: 'ansi-bg-black',
  41: 'ansi-bg-red',
  42: 'ansi-bg-green',
  43: 'ansi-bg-yellow',
  44: 'ansi-bg-blue',
  45: 'ansi-bg-magenta',
  46: 'ansi-bg-cyan',
  47: 'ansi-bg-white',
};

/** 高亮背景 100-107 → 语义类名 */
const BG_BRIGHT: Record<number, string> = {
  100: 'ansi-bg-bright-black',
  101: 'ansi-bg-bright-red',
  102: 'ansi-bg-bright-green',
  103: 'ansi-bg-bright-yellow',
  104: 'ansi-bg-bright-blue',
  105: 'ansi-bg-bright-magenta',
  106: 'ansi-bg-bright-cyan',
  107: 'ansi-bg-bright-white',
};

// eslint-disable-next-line no-control-regex -- ANSI 转义必须匹配 ESC (0x1b)
const ANSI_RE = /\x1b\[([0-9;]*)m/g;

export function ansiToSegments(src: string): AnsiSegment[] {
  const out: AnsiSegment[] = [];
  let bold = false;
  let fg: string | null = null;
  let bg: string | null = null;

  const classesOf = (): string[] => {
    const c: string[] = [];
    if (bold) c.push('ansi-bold');
    if (fg) c.push(fg);
    if (bg) c.push(bg);
    return c;
  };

  ANSI_RE.lastIndex = 0;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = ANSI_RE.exec(src)) !== null) {
    if (m.index > last) {
      out.push({ text: src.slice(last, m.index), classes: classesOf() });
    }
    const codes = (m[1] ?? '').split(';').filter(Boolean).map(Number);
    // 空码（即 ESC[m）与 0 都视为重置
    if (codes.length === 0 || codes.includes(0)) {
      bold = false;
      fg = null;
      bg = null;
    }
    for (const code of codes) {
      if (code === 0) {
        bold = false;
        fg = null;
        bg = null;
      } else if (code === 1) {
        bold = true;
      } else if (code === 22) {
        bold = false;
      } else if (code === 39) {
        fg = null;
      } else if (code === 49) {
        bg = null;
      } else if (FG_STD[code] !== undefined) {
        fg = FG_STD[code]!;
      } else if (FG_BRIGHT[code] !== undefined) {
        fg = FG_BRIGHT[code]!;
      } else if (BG_STD[code] !== undefined) {
        bg = BG_STD[code]!;
      } else if (BG_BRIGHT[code] !== undefined) {
        bg = BG_BRIGHT[code]!;
      }
      // 其余 SGR 码（斜体/下划线/256 色/真彩等）暂不支持，直接忽略
    }
    last = ANSI_RE.lastIndex;
  }

  if (last < src.length) {
    out.push({ text: src.slice(last), classes: classesOf() });
  }
  return mergeSameClasses(out);
}

function mergeSameClasses(segs: AnsiSegment[]): AnsiSegment[] {
  const out: AnsiSegment[] = [];
  for (const s of segs) {
    const prev = out[out.length - 1];
    if (prev && prev.classes.join('\x1f') === s.classes.join('\x1f')) {
      prev.text += s.text;
    } else {
      out.push({ text: s.text, classes: [...s.classes] });
    }
  }
  return out;
}

/** 纯文本化（用于复制到剪贴板 / 搜索） */
/** 纯文本化（用于复制到剪贴板 / 搜索） */
export function stripAnsi(src: string): string {
  // eslint-disable-next-line no-control-regex -- ANSI 转义必须匹配 ESC (0x1b)
  return src.replace(/\x1b\[[0-9;]*m/g, '');
}
