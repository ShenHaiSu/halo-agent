<template>
  <div class="p-2 space-y-1.5">
    <div class="font-mono app-text-small text-primary truncate" :title="execCommand">
      <span class="text-secondary select-none">$ </span>{{ execCommand }}
    </div>
    <div
      class="max-h-72 overflow-y-auto bg-[var(--tool-output-bg)] p-3 font-mono text-xs leading-relaxed text-[var(--tool-output-text)] select-text rounded whitespace-pre-wrap break-all border border-subtle"
      data-testid="terminal-output"
    >
      <span v-if="!displayOutput" class="text-secondary">等待输出...</span><span v-else>{{ displayOutput }}</span>
    </div>
    <div class="flex items-center justify-end gap-2">
      <n-button v-if="card.status === 'running'" size="tiny" type="error" ghost @click="handleKill">
        <template #icon><Square class="w-3 h-3" /></template>
        终止进程
      </n-button>
      <n-button v-else size="tiny" quaternary @click="handleCopy">
        <template #icon><Copy class="w-3 h-3" /></template>
        复制输出
      </n-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { Copy, Square } from 'lucide-vue-next';
import type { AgentToolCard } from '@features/agent/stores/model';
import { execCommandOf, parseToolArgs } from '@features/agent/utils/toolArgs';
import { useToolCardActions } from '@features/agent/composables/useToolCardActions';

/** 命令执行分支：命令行回显、终端输出、运行态终止、完成态复制。 */
const props = defineProps<{ card: AgentToolCard }>();

const actions = useToolCardActions(() => props.card);

const execCommand = computed(() => execCommandOf(parseToolArgs(props.card)));
const displayOutput = computed(() => actions.displayOutput.value);

function handleKill(): void {
  void actions.handleKill();
}

function handleCopy(): void {
  void actions.handleCopyText(displayOutput.value);
}
</script>
