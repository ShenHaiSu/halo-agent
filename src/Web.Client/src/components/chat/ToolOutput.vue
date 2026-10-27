<!-- src/Web.Client/src/components/chat/ToolOutput.vue -->
<!-- P5 D5-4 工具实时输出区：pre 显示截断 + "已截断"提示；ANSI 转义只显示纯文本
     （stripAnsi，避免样式爆炸）；每次更新自动滚动到底。 -->
<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { stripAnsi } from '@/utils/ansi';

const props = withDefaults(
  defineProps<{
    output: string;
    maxChars?: number;
  }>(),
  { maxChars: 4000 }
);

const expanded = ref(false);
const box = ref<HTMLElement | null>(null);

const truncated = computed(() => props.output.length > props.maxChars);
const shown = computed(() =>
  truncated.value && !expanded.value ? props.output.slice(0, props.maxChars) : props.output
);

// ANSI 只显示纯文本（避免 shell 颜色码样式爆炸）；pre 保留换行
const plain = computed(() => stripAnsi(shown.value));

watch(
  () => props.output,
  async () => {
    const el = box.value;
    if (!el) return;
    await nextTick();
    el.scrollTop = el.scrollHeight;
  }
);
</script>

<template>
  <div class="out-wrap">
    <pre ref="box" class="out-pre">{{ plain }}</pre>
    <div v-if="truncated && !expanded" class="out-truncated">
      <span>已截断（仅显示前 {{ maxChars }} 字符，共 {{ output.length }} 字符）</span>
      <button type="button" class="out-more" @click="expanded = true">显示全部</button>
    </div>
  </div>
</template>

<style scoped>
.out-wrap {
  min-width: 0;
}
.out-pre {
  margin: 0;
  padding: var(--space-2);
  background: hsl(var(--bg-subtle));
  border-radius: var(--radius-sm);
  font-family: var(--font-mono);
  font-size: var(--text-xs);
  line-height: 1.5;
  max-height: 360px;
  overflow: auto;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  color: hsl(var(--fg));
}
.out-truncated {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin-top: var(--space-1);
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
}
.out-more {
  background: transparent;
  border: 0;
  padding: 0;
  color: hsl(var(--accent));
  font-size: var(--text-xs);
  cursor: pointer;
}
.out-more:hover {
  text-decoration: underline;
}
</style>
