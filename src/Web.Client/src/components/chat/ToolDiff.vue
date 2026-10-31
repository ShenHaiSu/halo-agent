<!-- src/Web.Client/src/components/chat/ToolDiff.vue -->
<!-- P5 D5-4 文件改动的 diff 预览：从 output 提取 diff 块，按行级渲染；
     无 diff 块时不渲染。P11-F：提取放宽（围栏优先，其次裸 unified diff，
     见 ./toolDiff），合并后超 400 行截断 + “展开全部”按钮。
     加/删行分别用 success/danger-subtle 着色。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import type { DiffLine } from '@/utils/diff';
import { blockToLines, DIFF_MAX_LINES, extractDiffBlocks } from './toolDiff';

const props = defineProps<{ output: string }>();

const diffBlocks = computed<string[]>(() => extractDiffBlocks(props.output));

const expanded = ref(false);

const lines = computed<DiffLine[] | null>(() => {
  if (diffBlocks.value.length === 0) return null;
  const merged: DiffLine[] = [];
  for (const b of diffBlocks.value) merged.push(...blockToLines(b));
  return merged.length > 0 ? merged : null;
});

const truncated = computed(() => (lines.value?.length ?? 0) > DIFF_MAX_LINES);
const shownLines = computed(() =>
  lines.value && truncated.value && !expanded.value
    ? lines.value.slice(0, DIFF_MAX_LINES)
    : (lines.value ?? [])
);

function opClass(type: DiffLine['type']): string {
  if (type === 'add') return 'op-add';
  if (type === 'del') return 'op-del';
  return 'op-same';
}

function prefix(type: DiffLine['type']): string {
  if (type === 'add') return '+';
  if (type === 'del') return '-';
  return ' ';
}
</script>

<template>
  <section v-if="lines" class="diff-section" aria-label="文件改动预览">
    <div class="diff-head">
      <span class="diff-title">文件改动</span>
    </div>
    <div class="diff-body">
      <div v-for="(l, i) in shownLines" :key="i" class="diff-line" :class="opClass(l.type)">
        {{ prefix(l.type) }}{{ l.text || '' }}
      </div>
    </div>
    <button v-if="truncated && !expanded" type="button" class="diff-more" @click="expanded = true">
      展开全部（共 {{ lines.length }} 行）
    </button>
  </section>
</template>

<style scoped>
.diff-section {
  min-width: 0;
}
.diff-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin-bottom: var(--space-1);
}
.diff-title {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  text-transform: uppercase;
}
.diff-body {
  font-family: var(--font-mono);
  font-size: var(--text-xs);
  line-height: 1.5;
  background: hsl(var(--bg-subtle));
  border-radius: var(--radius-sm);
  max-height: 320px;
  overflow: auto;
  padding: var(--space-1) 0;
}
.diff-line {
  padding: 0 var(--space-2);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.op-add {
  background: hsl(var(--success-subtle));
  color: hsl(var(--success));
}
.op-del {
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
}
.op-same {
  color: hsl(var(--fg-muted));
}
.diff-more {
  margin-top: var(--space-1);
  background: transparent;
  border: 0;
  padding: 0;
  color: hsl(var(--accent));
  font-size: var(--text-xs);
  cursor: pointer;
}
.diff-more:hover {
  text-decoration: underline;
}
</style>
