import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';
import { blockHash } from '@features/session/utils/turnGroup';

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
const mdRenderCache = new Map<string, string>();
const MD_CACHE_MAX = 200;
export function renderMarkdown(content: string): string {
  const key = blockHash(content || '');
  const hit = mdRenderCache.get(key);
  if (hit !== undefined) return hit;
  let html: string;
  try {
    html = md.render(content || '');
  } catch (e) {
    console.warn('[markdown] render failed:', e);
    html = `<pre>${escapeHtml(content || '')}</pre>`;
  }
  mdRenderCache.set(key, html);
  if (mdRenderCache.size > MD_CACHE_MAX) {
    const oldest = mdRenderCache.keys().next();
    if (!oldest.done) mdRenderCache.delete(oldest.value);
  }
  return html;
}
export function renderStreamingText(content: string): string {
  return escapeHtml(content || '');
}
