<!-- src/Web.Client/src/components/chat/ToolDiff.vue -->
<!-- P5 D5-4 文件改动的 diff 预览：从 output 里提取 ```diff 围栏，按行级渲染；
     无 diff 块时不渲染。加/删行分别用 success/danger-subtle 着色。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { diffLines, type DiffLine } from '@/utils/diff';

const props = defineProps<{ output: string }>();

/**
 * 从 unified diff 文本还原 before/after，再走 diffLines 行级渲染。
 * 跳过 diff/index/---/+++/@@ 头行与 "\ No newline" 标记。
 */
function blockToLines(block: string): DiffLine[] {
  const before: string[] = [];
  const after: string[] = [];
  for (const raw of block.split('\n')) {
    if (
      raw.startsWith('diff ') ||
      raw.startsWith('index ') ||
      raw.startsWith('+++ ') ||
      raw.startsWith('--- ') ||
      raw.startsWith('@@ ') ||
      raw.startsWith('\\ No newline')
    ) {
      continue;
    }
    if (raw.startsWith('+') && !raw.startsWith('+++')) {
      after.push(raw.slice(1));
    } else if (raw.startsWith('-') && !raw.startsWith('---')) {
      before.push(raw.slice(1));
    } else if (raw.startsWith(' ')) {
      before.push(raw.slice(1));
      after.push(raw.slice(1));
    } else if (raw === '') {
      continue;
    } else {
      before.push(raw);
      after.push(raw);
    }
  }
  if (before.length === 0 && after.length === 0) return [];
  return diffLines(before.join('\n'), after.join('\n'));
}

const diffBlocks = computed<string[]>(() => {
  const out: string[] = [];
  const re = /```diff\r?\n([\s\S]*?)```/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(props.output)) !== null) {
    if (m[1] && m[1].trim()) out.push(m[1]);
  }
  return out;
});

const lines = computed<DiffLine[] | null>(() => {
  if (diffBlocks.value.length === 0) return null;
  const merged: DiffLine[] = [];
  for (const b of diffBlocks.value) merged.push(...blockToLines(b));
  return merged.length > 0 ? merged : null;
});

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
      <div v-for="(l, i) in lines" :key="i" class="diff-line" :class="opClass(l.type)">
        {{ prefix(l.type) }}{{ l.text || '' }}
      </div>
    </div>
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
</style>
