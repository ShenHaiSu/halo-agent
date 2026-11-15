<template>
  <div class="font-mono app-text-small leading-relaxed">
    <div
      v-for="(line, idx) in visibleDiffLines"
      :key="idx"
      class="flex items-start gap-2 px-2.5 py-px whitespace-pre-wrap break-all border-l-2"
      :class="
        line.kind === 'del'
          ? 'bg-rose-500/15 text-[var(--diff-del-text)] border-rose-500'
          : line.kind === 'add'
            ? 'bg-emerald-500/15 text-[var(--diff-add-text)] border-emerald-500'
            : 'text-[var(--diff-ctx-text)] border-transparent'
      "
    >
      <span class="w-4 shrink-0 select-none opacity-70">{{ line.kind === 'del' ? '-' : line.kind === 'add' ? '+' : ' ' }}</span>
      <span class="flex-1 min-w-0">{{ line.text }}</span>
    </div>
    <button
      v-if="diffLines.length > 15 && !showAllDiff"
      type="button"
      class="w-full py-1 text-center text-accent hover:underline text-[11px]"
      @click="showAllDiff = true"
    >
      展开全部 {{ diffLines.length }} 行变更
    </button>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { buildDiffLines } from '@features/agent/utils/toolDiff';

/** 文件改动差异视图：edit 按旧删新増，write 按正文展开，超 15 行折叠。 */
const props = defineProps<{ args: Record<string, unknown>; functionName: string }>();

const showAllDiff = ref(false);

const diffLines = computed(() => buildDiffLines(props.args, props.functionName));
const visibleDiffLines = computed(() => (showAllDiff.value ? diffLines.value : diffLines.value.slice(0, 15)));
</script>
