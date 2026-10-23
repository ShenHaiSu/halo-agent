import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';
import { advanceBlocks, checkJoin, createBlockCache, renderBlockHtml, type BlockCache } from './markdownBlocks';
import { classifySource, overflowHint } from './renderContentLimits';

export function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
const md: MarkdownIt = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  highlight: (code: string, lang: string): string => {
    try {
      if (lang && hljs.getLanguage(lang)) return hljs.highlight(code, { language: lang }).value;
      return escapeHtml(code);
    } catch (e) {
      console.warn('[markdown] highlight failed:', e);
      return escapeHtml(code);
    }
  },
});
const defaultLinkOpenRender = md.renderer.rules.link_open;
md.renderer.rules.link_open = (tokens, idx, options, env, self): string => {
  try {
    tokens[idx].attrSet('target', '_blank');
    tokens[idx].attrSet('rel', 'noopener noreferrer');
  } catch {
    /* ignore */
  }
  if (defaultLinkOpenRender) return defaultLinkOpenRender(tokens, idx, options, env, self);
  return self.renderToken(tokens, idx, options);
};

const blockCaches = new Map<string, BlockCache>();
const BLOCK_CACHE_MAX = 200;
function cacheOf(key: string): BlockCache {
  let c = blockCaches.get(key);
  if (!c) {
    c = createBlockCache();
    blockCaches.set(key, c);
    if (blockCaches.size > BLOCK_CACHE_MAX) {
      const oldest = blockCaches.keys().next();
      if (!oldest.done) blockCaches.delete(oldest.value);
    }
  }
  return c;
}
/**
 * 整源 renderMarkdown 语义已替换为块管线：advanceBlocks 只重切 tail，
 * settled 块零重跑；超限源降级纯文本 + 提示。保留同名导出兼容旧调用点。
 */
export function renderMarkdown(content: string): string {
  const src = content || '';
  if (classifySource(src) !== 'ok') {
    return `<pre>${escapeHtml(src)}</pre><div class="markdown-fallback-hint">${overflowHint(classifySource(src))}</div>`;
  }
  try {
    const key = 'default';
    const next = advanceBlocks(cacheOf(key), src);
    blockCaches.set(key, next);
    if (!checkJoin(src, next.blocks)) return renderBlockHtml(src);
    return next.blocks.map((b) => renderBlockHtml(b.raw)).join('');
  } catch (e) {
    console.warn('[markdown] render failed:', e);
    return `<pre>${escapeHtml(src)}</pre>`;
  }
}
export function renderStreamingText(content: string): string {
  return escapeHtml(content || '');
}
