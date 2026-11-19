import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';
import { highlightCode } from './codeHighlight';
import { overflowHint, type OverflowKind } from './renderContentLimits';
/** bigChange P2-2: markdown block splitter — shares one markdown-it instance with renderer. */

function escapeHtmlLocal(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

export interface MarkdownBlockSlice {
  raw: string;
  start: number;
  settled: boolean;
}

interface BlockCache {
  consumed: string;
  blocks: MarkdownBlockSlice[];
  singleUnit: boolean;
}

/**
 * 流式态与稳态共用的唯一 markdown-it 实例。
 *
 * M1-1：两侧必须同源——历史区（renderMarkdown）与活动容器（MarkdownStreamBlocks）
 * 都经 renderBlockHtml 走这里。highlight 钩子是「同源」的关键一环：
 * 缺它时带 language-x 的代码块在流式态渲染为 `<code class="language-x">`，
 * 毕业进历史区后重渲染出 `<span class="hljs-keyword">` 等 token，
 * 表现为「回复结束时代码块颜色/字宽跳变」（根因 R1）。
 */
export const sharedMd: MarkdownIt = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  highlight: (code: string, lang: string): string => {
    try {
      if (lang && hljs.getLanguage(lang)) return hljs.highlight(code, { language: lang }).value;
      return escapeHtmlLocal(code);
    } catch (e) {
      console.warn('[markdown-block] highlight failed:', e);
      return escapeHtmlLocal(code);
    }
  },
});

try {
  const linkOpen = sharedMd.renderer.rules.link_open;
  sharedMd.renderer.rules.link_open = (tokens, idx, options, env, self): string => {
    try {
      tokens[idx].attrSet('target', '_blank');
      tokens[idx].attrSet('rel', 'noopener noreferrer');
    } catch {
      /* ignore */
    }
    if (linkOpen) return linkOpen(tokens, idx, options, env, self);
    return self.renderToken(tokens, idx, options);
  };
} catch {
  /* ignore */
}

/**
 * 代码块复制：按钮由渲染器注入，点击由容器级事件委托接管。
 * 代码原文始终从 <code> 的 textContent 读取，不在 HTML 中重复塞入 —— 高亮只插入 span，
 * textContent 仍是纯代码，既准确又避免二次转义 / 注入面。
 * 注册在 sharedMd 上，流式态与稳态共用同一实例，保证交接时代码块结构一致。
 */
const COPY_BUTTON_CLASS = 'md-code-copy';
const CODE_SHELL_CLASS = 'md-code-shell';

const COPY_ICON_SVG =
  '<svg class="md-code-icon md-code-icon-copy" viewBox="0 0 24 24" fill="none" stroke="currentColor" ' +
  'stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
  '<rect width="14" height="14" x="8" y="8" rx="2" ry="2"/>' +
  '<path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg>';
const CHECK_ICON_SVG =
  '<svg class="md-code-icon md-code-icon-check" viewBox="0 0 24 24" fill="none" stroke="currentColor" ' +
  'stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
  '<path d="M20 6 9 17l-5-5"/></svg>';

/** 属性值转义：语言名来自用户输入，必须同时转义引号，杜绝属性逃逸。 */
function escapeHtmlAttr(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function renderCodeWithCopy(tokens: Array<{ content?: string; info?: string }>, idx: number): string {
  const token = tokens[idx];
  const raw = String(token?.content ?? '');
  const info = String(token?.info ?? '').trim();
  const lang = info.split(/\s+/)[0] || '';
  // 与 highlight 钩子同源走缓存口径；未知语言 / 超长自动退化为转义纯文本。
  const codeInner = highlightCode(raw, lang).html;
  const safeLang = lang ? escapeHtmlAttr(lang) : '';
  const codeClass = safeLang ? ` class="language-${safeLang}"` : '';
  return (
    `<div class="${CODE_SHELL_CLASS}">` +
    `<button type="button" class="${COPY_BUTTON_CLASS}" data-code-copy aria-label="复制代码" title="复制代码">` +
    `${COPY_ICON_SVG}${CHECK_ICON_SVG}<span class="md-code-copy-label">复制</span>` +
    `</button>` +
    `<pre><code${codeClass}>${codeInner}</code></pre>` +
    `</div>`
  );
}
sharedMd.renderer.rules.fence = renderCodeWithCopy;
sharedMd.renderer.rules.code_block = renderCodeWithCopy;

const DEFINITION_RE = /(^|\n)\[\^[^\]]+\]:|\n\[[^\]]+\]:\s*\S/;

function hasDefinitionSource(source: string): boolean {
  return DEFINITION_RE.test(source);
}

/** Split source into block slices by byte offsets of the same markdown-it token stream. */
export function splitBlocks(source: string): MarkdownBlockSlice[] {
  if (!source) return [];
  if (hasDefinitionSource(source)) {
    return [{ raw: source, start: 0, settled: false }];
  }
  let tokens: Array<{ map?: [number, number] | null }> = [];
  try {
    tokens = sharedMd.parse(source, {}) as Array<{ map?: [number, number] | null }>;
  } catch {
    return [{ raw: source, start: 0, settled: false }];
  }
  const lines = source.split('\n');
  const lineStart: number[] = [];
  let acc = 0;
  for (let i = 0; i < lines.length; i++) {
    lineStart.push(acc);
    acc += lines[i].length + 1;
  }
  const slices: MarkdownBlockSlice[] = [];
  let lastStart = 0;
  for (const t of tokens) {
    if (!t.map) continue;
    const begin = lineStart[t.map[0]] ?? 0;
    const endLine = Math.min(t.map[1], lines.length);
    const end = endLine >= lines.length ? source.length : (lineStart[endLine] ?? source.length);
    if (begin < lastStart) continue;
    if (begin > lastStart) {
      slices.push({ raw: source.slice(lastStart, begin), start: lastStart, settled: true });
    }
    slices.push({ raw: source.slice(begin, Math.max(end, begin)), start: begin, settled: false });
    lastStart = Math.max(end, begin);
  }
  if (lastStart < source.length) {
    slices.push({ raw: source.slice(lastStart), start: lastStart, settled: false });
  }
  if (slices.length === 0) return [{ raw: source, start: 0, settled: false }];
  // Mark all but the last as settled.
  for (let i = 0; i < slices.length - 1; i++) slices[i].settled = true;
  return slices;
}

/** advanceBlocks: appended = source.startsWith(consumed); normal path only re-slices tail; definition source -> singleUnit latch (one-way). */
export function advanceBlocks(cache: BlockCache | null, source: string): BlockCache {
  if (!cache) {
    const singleUnit = hasDefinitionSource(source);
    return { consumed: source, blocks: splitBlocks(source), singleUnit };
  }
  if (cache.singleUnit) {
    // 单向锁存：definition 源一旦 singleUnit，后续仍整源一块。
    return { consumed: source, blocks: [{ raw: source, start: 0, settled: false }], singleUnit: true };
  }
  if (hasDefinitionSource(source)) {
    return { consumed: source, blocks: [{ raw: source, start: 0, settled: false }], singleUnit: true };
  }
  const appended = source.startsWith(cache.consumed);
  if (appended && cache.blocks.length > 0) {
    const keep = cache.blocks.slice(0, -1);
    const tailStart = cache.blocks[cache.blocks.length - 1].start;
    const tailSource = source.slice(tailStart);
    const tailBlocks = splitBlocks(tailSource).map((b) => ({ ...b, start: b.start + tailStart }));
    for (let i = 0; i < keep.length; i++) keep[i] = { ...keep[i], settled: true };
    return { consumed: source, blocks: [...keep, ...tailBlocks], singleUnit: false };
  }
  return { consumed: source, blocks: splitBlocks(source), singleUnit: false };
}

export function createBlockCache(): BlockCache {
  return { consumed: '', blocks: [], singleUnit: false };
}

export type { BlockCache };

/** Invariant check: slices joined (bytes) === source. */
export function checkJoin(source: string, blocks: MarkdownBlockSlice[]): boolean {
  return blocks.map((b) => b.raw).join('') === source;
}

export function renderBlockHtml(raw: string): string {
  try {
    return sharedMd.render(raw);
  } catch (e) {
    console.warn('[markdown-block] render failed:', e);
    return `<pre>${escapeHtmlLocal(raw)}</pre>`;
  }
}

/**
 * M1-2：超限降级块的**统一外观**（流式与稳态共用同一套结构与 class）。
 *
 * 为什么要统一：流式门槛 `STREAM_TAIL_LIMIT`(32KB) 低于稳态 `HIGHLIGHT_CHAR_LIMIT`(100KB)，
 * 因此 32~100KB 的代码块必然经历「流式降级纯文本 → 毕业后高亮 HTML」的交接。
 * 阈值差异是流式渲染的性能保护，保留不动（详见 docs 99 §8）；
 * 但两侧用同一个函数产出同一套 `.markdown-fallback` 结构，
 * 交接时背景/边框/字号完全一致，视觉上是「文字变彩色」而不是「整块重画」。
 */
export function renderFallbackHtml(raw: string, kind: OverflowKind): string {
  const hint = overflowHint(kind);
  const hintHtml = hint ? `<div class="markdown-fallback-hint">${hint}</div>` : '';
  return `<div class="markdown-fallback" data-overflow-kind="${kind}"><pre>${escapeHtmlLocal(raw)}</pre>${hintHtml}</div>`;
}
