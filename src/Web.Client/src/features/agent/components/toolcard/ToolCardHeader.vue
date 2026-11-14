<template>
  <div
    class="flex items-center gap-2 px-2.5 py-1.5 cursor-pointer select-none hover:bg-elevated/60 transition-colors"
    @click="emit('toggle')"
  >
    <component :is="iconComponent" class="w-3.5 h-3.5 shrink-0 text-accent" />
    <span class="font-mono font-medium text-primary truncate">{{ card.functionName }}</span>

    <n-tag v-if="isExec" :type="runnerType" size="tiny" :bordered="false" class="font-mono shrink-0">
      {{ runnerLabel }}
    </n-tag>

    <span v-if="headText" class="font-mono text-secondary truncate flex-1 min-w-0" :title="headText">
      {{ headText }}
    </span>

    <span v-if="isFileEdit" class="font-mono text-[11px] shrink-0">
      <span class="text-[var(--stat-add-text)]">+{{ addedLines }}</span>
      <span> </span>
      <span class="text-[var(--stat-del-text)]">-{{ removedLines }}</span>
    </span>

    <n-tag
      :type="effectiveStatus === 'done' ? 'success' : effectiveStatus === 'error' ? 'error' : 'info'"
      size="tiny"
      :bordered="false"
      class="shrink-0"
      :role="isExec ? 'status' : undefined"
    >
      {{ effectiveStatus === 'done' ? '已完成' : effectiveStatus === 'error' ? '失败' : '执行中' }}
    </n-tag>
    <span v-if="card.durationMs !== null && card.durationMs !== undefined" class="font-mono text-[11px] text-secondary shrink-0">
      {{ card.durationMs }}ms
    </span>
    <span
      v-if="isExec && card.exitCode !== null && card.exitCode !== undefined"
      class="font-mono text-[11px] shrink-0"
      :class="card.exitCode === 0 ? 'text-[var(--stat-add-text)]' : 'text-[var(--stat-del-text)]'"
    >
      Exit: {{ card.exitCode }}
    </span>
    <ChevronDown class="w-3.5 h-3.5 text-secondary shrink-0 transition-transform" :class="expanded ? 'rotate-180' : ''" />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { ChevronDown, FileDiff, FilePlus2, FileText, FolderSearch, Search, Terminal, Wrench } from 'lucide-vue-next';
import type { AgentToolCard } from '@features/agent/stores/model';
import type { ToolCardStatus } from '@features/agent/utils/toolHeadSummary';
import { parseToolArgs, stringArg } from '@features/agent/utils/toolArgs';
import { buildDiffLines, countDiffLines } from '@features/agent/utils/toolDiff';

/** 工具卡头行：图标、执行器徽标、头文案、增删计数、状态、耗时，一处装配。 */
const props = defineProps<{
  card: AgentToolCard;
  headText: string;
  effectiveStatus: ToolCardStatus;
  expanded: boolean;
}>();

const emit = defineEmits<{ toggle: [] }>();

const isFileEdit = computed(() => props.card.functionName === 'edit_file' || props.card.functionName === 'write_file');
const isExec = computed(() => props.card.functionName === 'exec_command');

const iconComponent = computed(() => {
  switch (props.card.functionName) {
    case 'edit_file':
      return FileDiff;
    case 'write_file':
      return FilePlus2;
    case 'read_file':
      return FileText;
    case 'list_dir':
      return FolderSearch;
    case 'grep_search':
      return Search;
    case 'exec_command':
      return Terminal;
    default:
      return Wrench;
  }
});

const runnerLabel = computed(() => {
  const v = stringArg(parseToolArgs(props.card), 'runner');
  return v.toLowerCase() === 'python' ? 'PYTHON' : 'CMD.EXE';
});

const runnerType = computed<'info' | 'success'>(() => (runnerLabel.value === 'PYTHON' ? 'success' : 'info'));

const diffCounts = computed(() => countDiffLines(buildDiffLines(parseToolArgs(props.card), props.card.functionName)));
const addedLines = computed(() => diffCounts.value.added);
const removedLines = computed(() => diffCounts.value.removed);
</script>
