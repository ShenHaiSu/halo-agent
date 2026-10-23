import { checkJoin, renderBlockHtml, renderFallbackHtml, splitBlocks } from './markdownBlocks';
import { classifySource } from './renderContentLimits';

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
