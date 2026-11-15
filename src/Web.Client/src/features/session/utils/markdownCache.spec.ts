// need02-P4：Markdown 渲染缓存单测（p4-md：输出一致 + 命中 + LRU 有界）。
import { beforeEach, describe, expect, it } from 'vitest';
import { clearMarkdownCache, markdownCacheStats, renderMarkdown, renderMarkdownCached } from './markdownRender';

beforeEach(() => {
  clearMarkdownCache();
});

describe('need02-P4 renderMarkdownCached', () => {
  it('输出与直跑逐字一致', () => {
    const src = '# 标题\n\n正文 **加粗** `代码` [链接](https://example.com)\n\n- 列表项\n';
    expect(renderMarkdownCached(src)).toBe(renderMarkdown(src));
    expect(renderMarkdownCached('')).toBe(renderMarkdown(''));
  });

  it('二次命中走缓存（hits 递增，输出一致）', () => {
    const src = 'hello **world**';
    const first = renderMarkdownCached(src);
    const s1 = markdownCacheStats();
    expect(s1.misses).toBe(1);
    expect(s1.hits).toBe(0);
    const second = renderMarkdownCached(src);
    expect(second).toBe(first);
    const s2 = markdownCacheStats();
    expect(s2.hits).toBe(1);
    expect(s2.size).toBe(1);
  });

  it('不同内容分键缓存', () => {
    renderMarkdownCached('aaa');
    renderMarkdownCached('bbb');
    const s = markdownCacheStats();
    expect(s.size).toBe(2);
    expect(s.misses).toBe(2);
  });

  it('LRU 有界 200（超量淘汰最老）', () => {
    for (let i = 0; i < 210; i++) renderMarkdownCached(`内容-${i}-` + 'x'.repeat(8));
    const s = markdownCacheStats();
    expect(s.size).toBeLessThanOrEqual(200);
    // 最老的 key 已被淘汰：再跑一次是 miss。
    const before = markdownCacheStats().misses;
    renderMarkdownCached('内容-0-xxxxxxxx');
    expect(markdownCacheStats().misses).toBe(before + 1);
  });
});
