import MarkdownIt from 'markdown-it';
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

export const sharedMd: MarkdownIt = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
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
