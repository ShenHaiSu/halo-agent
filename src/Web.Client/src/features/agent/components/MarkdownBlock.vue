<template>
  <div v-if="kind === 'pre'" class="markdown-fallback" :title="hint">
    <div v-if="hint" class="markdown-fallback-hint">
      {{ hint }}
    </div>
    <pre>{{ raw }}</pre>
  </div>
  <div v-else class="markdown-block" v-html="html" />
</template>
<script setup lang="ts">
import { computed } from 'vue';
import { classifyBlock, overflowHint } from '@features/session/utils/renderContentLimits';
import { escapeHtml } from '@features/session/utils/markdownRender';
import { renderBlockHtml } from '@features/session/utils/markdownBlocks';

// Block memo: raw 不变跳过（父层 v-memo/key 保证；此处 computed 缓存渲染结果）
const props = defineProps<{
  raw: string;
  streaming?: boolean;
}>();

const kind = computed(() => (classifyBlock(props.raw, !!props.streaming) === 'ok' ? 'html' : 'pre'));
const hint = computed(() => (kind.value === 'pre' ? overflowHint(classifyBlock(props.raw, !!props.streaming)) : ''));
const html = computed(() => {
  if (kind.value === 'pre') return '';
  try {
    return renderBlockHtml(props.raw);
  } catch (e) {
    console.warn('[MarkdownBlock] render failed:', e);
    return `<pre>${escapeHtml(props.raw)}</pre>`;
  }
});
</script>
<style scoped>
.markdown-fallback-hint {
  font-size: 11px;
  opacity: 0.7;
  margin-bottom: 4px;
}
.markdown-block :deep(pre) {
  overflow-x: auto;
}
</style>
