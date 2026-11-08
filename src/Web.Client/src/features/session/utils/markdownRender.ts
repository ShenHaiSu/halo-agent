import { checkJoin, renderBlockHtml, renderFallbackHtml, splitBlocks } from './markdownBlocks';
import { classifySource } from './renderContentLimits';
import { blockHash } from './turnGroup';

export function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/**
 * 稳态整源渲染（历史区每个 assistant 消息各调一次，调用量随历史长度线性增长）。
 *
 * 渲染实例：与流式态共用 `markdownBlocks.ts` 的 `sharedMd` 单实例（已注册 highlight 钩子），
 * 保证「活动容器 → 历史区」交接时同一段 Markdown 产出相同 token 结构，零重排。
 *
 * 为什么**不用** advanceBlocks 增量缓存：
 * advanceBlocks 的 cache 是「单一 consumed→blocks 序列」，只在**同一段文本持续追加**时有意义
 * （即流式场景，见 MarkdownStreamBlocks.vue）。历史区每次传入的是不同消息的不同文本，
 * 若共用一个 key，每次调用都会判定"非追加"并整体重建——没有命中收益，反而白做一次
 * startsWith 比较。因此稳态直接整源切块渲染。
 */
export function renderMarkdown(content: string): string {
  const src = content || '';
  if (classifySource(src) !== 'ok') {
    // 与流式降级块共用同一套外观（.markdown-fallback），交接时不再「整块重画」（M1-2）。
    return renderFallbackHtml(src, 'source-text');
  }
  try {
    const blocks = splitBlocks(src);
    if (!checkJoin(src, blocks)) return renderBlockHtml(src);
    return blocks.map((b) => renderBlockHtml(b.raw)).join('');
  } catch (e) {
    console.warn('[markdown] render failed:', e);
    return `<pre>${escapeHtml(src)}</pre>`;
  }
}

/** 流式逃生路径（localStorage 'halo-stream-md' = 'off' 时）：纯文本，不做 Markdown 解析。 */
export function renderStreamingText(content: string): string {
  return escapeHtml(content || '');
}

// ---- need02-P4（p4-md）：稳态渲染缓存 ----
// 长轮次里同一正文会被反复 computed（打字机 + 交接 + 展开收起），每次整源重跑。
// 键 = blockHash(content)（FNV-1a + 长度，复用 turnGroup.ts），LRU 200 条。纯渲染缓存，
// 不改变任何输出；p4-md=off 时调用方走 renderMarkdown 直跑（见 MessageTimeline.renderHtml）。

const MD_CACHE_MAX = 200;
const mdCache = new Map<string, string>();
let mdCacheHits = 0;
let mdCacheMisses = 0;

export function renderMarkdownCached(content: string): string {
  const src = content || '';
  const key = blockHash(src);
  const hit = mdCache.get(key);
  if (hit !== undefined) {
    mdCacheHits++;
    // LRU：命中上浮（delete+set 搬到尾部）。
    mdCache.delete(key);
    mdCache.set(key, hit);
    return hit;
  }
  mdCacheMisses++;
  const html = renderMarkdown(src);
  mdCache.set(key, html);
  if (mdCache.size > MD_CACHE_MAX) {
    const oldest = mdCache.keys().next();
    if (!oldest.done) mdCache.delete(oldest.value);
  }
  return html;
}

/** 缓存观测（测试/调试用）。 */
export function markdownCacheStats(): { size: number; hits: number; misses: number } {
  return { size: mdCache.size, hits: mdCacheHits, misses: mdCacheMisses };
}

/** 清空缓存（测试隔离/内存兜底用）。 */
export function clearMarkdownCache(): void {
  mdCache.clear();
  mdCacheHits = 0;
  mdCacheMisses = 0;
}
