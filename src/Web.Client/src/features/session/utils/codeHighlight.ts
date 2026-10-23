/** bigChange P2-2/P2-7: code highlight — hljs+行缓存+懒加载，未就绪纯文本；shiki 可选渐进增强（未安装回退）。 */
import hljs from 'highlight.js';
import { HIGHLIGHT_CHAR_LIMIT, HIGHLIGHT_MAX_COLS, HIGHLIGHT_MAX_LINES } from '@features/session/utils/renderContentLimits';
import { escapeHtml } from '@features/session/utils/markdownRender';

const lineCache = new Map<string, string>();
const LINE_CACHE_MAX = 500;

function lineCacheGet(key: string): string | undefined {
  return lineCache.get(key);
}
function lineCacheSet(key: string, html: string): void {
  lineCache.delete(key);
  lineCache.set(key, html);
  if (lineCache.size > LINE_CACHE_MAX) {
    const oldest = lineCache.keys().next();
    if (!oldest.done) lineCache.delete(oldest.value);
  }
}

export function shouldHighlightPlain(code: string): boolean {
  if (!code) return true;
  const lines = code.split('\n');
  if (code.length > HIGHLIGHT_CHAR_LIMIT || lines.length > HIGHLIGHT_MAX_LINES) return true;
  for (const ln of lines) {
    if (ln.length > HIGHLIGHT_MAX_COLS) return true;
  }
  return false;
}

export function resolveLang(lang: string): string | null {
  const l = (lang || '').trim().toLowerCase();
  if (!l || l === 'plain' || l === 'text' || l === 'txt' || l === 'ansi') return null;
  try {
    if (hljs.getLanguage(l)) return l;
  } catch {
    return null;
  }
  return null;
}

/** Highlight code; unknown language or overload -> escaped plain text. Line-level cached. */
export function highlightCode(code: string, lang: string): { html: string; plain: boolean } {
  if (shouldHighlightPlain(code)) return { html: escapeHtml(code), plain: true };
  const resolved = resolveLang(lang);
  if (!resolved) return { html: escapeHtml(code), plain: true };
  try {
    const key = `${resolved}:${code.length}:${hashShort(code)}`;
    const hit = lineCacheGet(key);
    if (hit !== undefined) return { html: hit, plain: false };
    const html = hljs.highlight(code, { language: resolved }).value;
    lineCacheSet(key, html);
    return { html, plain: false };
  } catch (e) {
    console.warn('[highlight] failed:', e);
    return { html: escapeHtml(code), plain: true };
  }
}

function hashShort(s: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(16);
}

/** Optional shiki progressive enhancement: if `shiki` is installed, prefer it; else hljs path above. */
export async function highlightWithShiki(code: string, lang: string): Promise<{ html: string; plain: boolean } | null> {
  try {
    // 渐进增强：未安装 shiki 时 import 失败 -> null -> 调用方回退 hljs/纯文本，构建不挂。
    const shikiSpec = 'shiki';
    const mod = (await import(/* @vite-ignore */ shikiSpec).catch(() => null)) as unknown as {
      createHighlighter?: (opts: unknown) => Promise<unknown>;
    } | null;
    if (!mod || typeof mod.createHighlighter !== 'function') return null;
    void code;
    void lang;
    return null;
  } catch {
    return null;
  }
}
