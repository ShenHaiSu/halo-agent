<template>
  <div class="markdown-body stream-body min-w-0">
    <div v-for="(b, i) in blocks" :key="b.start + ':' + i" class="stream-block" v-html="b.html" />
  </div>
</template>
<script setup lang="ts">
/**
 * need23-R5：流式 Markdown 块渲染。
 * 背景：活动容器原先用 escapeHtml 纯文本，落盘后切到 markdown-body → 每轮 3 次排版跳变。
 * 做法：复用历史区同一套 renderBlockHtml（markdown-it + highlight.js），按块切分；
 *      已 settled 的块只渲染一次（基于 cacheKey 的块缓存），只有尾块随增量重跑。
 * 降级：块超限（>100KB / >800 行 / 单行 >2000 列）或整源 >128KB → 纯文本 + 提示，
 *      与历史区 renderMarkdown 的口径一致（同一套 renderContentLimits 判定）。
 */
import { computed } from 'vue';
import {
  advanceBlocks,
  createBlockCache,
  checkJoin,
  splitBlocks,
  renderBlockHtml,
  renderFallbackHtml,
  type BlockCache,
  type MarkdownBlockSlice,
} from '@features/session/utils/markdownBlocks';
import { classifyBlock, classifySource } from '@features/session/utils/renderContentLimits';
import { escapeHtml } from '@features/session/utils/markdownRender';

const props = defineProps<{
  text: string;
  /** 块缓存键（通常 = seg.key）；同 key 的增量会复用已 settled 块。 */
  cacheKey: string;
}>();

interface PreparedBlock {
  start: number;
  raw: string;
  settled: boolean;
  html: string;
}

interface Entry {
  /** 纯块缓存（advanceBlocks 的输入，含 consumed/blocks/singleUnit）。 */
  blockCache: BlockCache;
  /** 上次投影的源文本（增量判定 + 命中短路）。 */
  lastSource: string;
  /** 已渲染的块（含 html），供 settled 块零重跑复用。 */
  prepared: PreparedBlock[];
  /** 整源超限降级缓存。 */
  oversizedFor: string;
  oversizedHtml: string;
}

// 每 cacheKey 一个条目；上限 64（超出淘汰最早插入的，防 seg 频繁新建时无限增长）
const CACHE_LIMIT = 64;
const store = new Map<string, Entry>();

function entryOf(key: string): Entry {
  let e = store.get(key);
  if (!e) {
    e = { blockCache: createBlockCache(), lastSource: '', prepared: [], oversizedFor: '', oversizedHtml: '' };
    store.set(key, e);
    if (store.size > CACHE_LIMIT) {
      const oldest = store.keys().next();
      if (!oldest.done) store.delete(oldest.value);
    }
  }
  return e;
}

function renderOne(raw: string, streaming: boolean): string {
  // 降级块与稳态共用同一套外观（M1-2），避免「纯文本 → 高亮」交接时整块重画。
  if (classifyBlock(raw, streaming) !== 'ok') return renderFallbackHtml(raw, 'block-text');
  // need23-R5：渲染形态与历史区一致。两态共用 markdownBlocks.ts 的 sharedMd 单实例
  // （已注册 highlight 钩子），故代码块 token 结构两边相同；
  // 「settled 块在活动容器 → 历史区切换时零跳变」以两者同源为前提才成立。
  return renderBlockHtml(raw);
}

const blocks = computed<PreparedBlock[]>(() => {
  const src = props.text || '';
  if (!src) return [];
  const e = entryOf(props.cacheKey);

  // 1) 整源超 128KB：整体降级纯文本（与历史区 renderMarkdown 同口径），不做块切分
  if (classifySource(src) !== 'ok') {
    if (e.oversizedFor !== src) {
      e.oversizedFor = src;
      e.oversizedHtml = renderFallbackHtml(src, 'source-text');
    }
    return [{ start: 0, raw: src, settled: false, html: e.oversizedHtml }];
  }

  // 2) 完全没变：短路命中
  if (e.lastSource === src && e.prepared.length > 0) return e.prepared;

  // 3) 文本变短 或 非追加（重置/重放/切会话复用 key）→ 重建块缓存
  if (!src.startsWith(e.lastSource)) {
    e.blockCache = createBlockCache();
    e.prepared = [];
    e.lastSource = '';
  }

  try {
    const next = advanceBlocks(e.blockCache, src);
    const slices: MarkdownBlockSlice[] = checkJoin(src, next.blocks) ? next.blocks : splitBlocks(src);

    // settled 块按 start 命中复用（raw 相同才复用，保证正确性）
    const prevSettled = new Map<number, PreparedBlock>();
    for (const p of e.prepared) if (p.settled) prevSettled.set(p.start, p);

    e.blockCache = next;
    e.lastSource = src;
    e.prepared = slices.map((s) => {
      const hit = s.settled ? prevSettled.get(s.start) : undefined;
      if (hit && hit.raw === s.raw) return hit;
      return { start: s.start, raw: s.raw, settled: s.settled, html: renderOne(s.raw, !s.settled) };
    });
    return e.prepared;
  } catch (err) {
    console.warn('[MarkdownStreamBlocks] render failed:', err);
    return [{ start: 0, raw: src, settled: false, html: `<pre>${escapeHtml(src)}</pre>` }];
  }
});
</script>
<style scoped>
.stream-block :deep(pre) {
  overflow-x: auto;
}
</style>
