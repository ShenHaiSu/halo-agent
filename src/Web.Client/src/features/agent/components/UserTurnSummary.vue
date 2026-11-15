<!-- need02-P2：一行汇总纯展示（D1=B：上折叠行；字数不进折叠行，0 值隐藏）。 -->
<template>
  <div
    class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 overflow-hidden"
    :class="turn.failedCount > 0 ? 'border-rose-500' : 'border-indigo-500/70'"
    data-testid="user-turn-summary"
    :data-turn-key="turn.key"
  >
    <div class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none" @click="emit('toggle', turn.key)">
      <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
        <span aria-hidden="true">{{ icon }}</span>
        <Loader2 v-if="turn.isRunning && !readingWindow" class="w-3 h-3.5 shrink-0 animate-spin text-indigo-400" />
        <span class="truncate">过程内容<span v-if="summaryText"> · {{ summaryText }}</span></span>
        <span
          v-if="turn.failedCount > 0"
          class="shrink-0 flex items-center gap-0.5"
          :title="`${turn.failedCount} 个工具调用失败`"
          data-testid="user-turn-failed-dots"
        >
          <template v-if="turn.failedCount <= 2">
            <span v-for="i in turn.failedCount" :key="i" class="w-1.5 h-1.5 rounded-full bg-rose-500" data-testid="user-turn-failed-dot" />
          </template>
          <template v-else>
            <span class="w-1.5 h-1.5 rounded-full bg-rose-500" data-testid="user-turn-failed-dot" />
            <span class="font-mono text-[10px] leading-none text-rose-500" data-testid="user-turn-failed-dot-count">×{{ turn.failedCount }}</span>
          </template>
        </span>
        <span class="shrink-0 font-mono text-[10px] opacity-70" data-testid="user-turn-phase">{{ turn.phaseLabel }}</span>
      </span>
      <span class="flex items-center gap-1 text-[11px] text-[var(--reasoning-text)] opacity-80 shrink-0">
        <span>{{ expanded ? '点击折叠' : '点击展开' }}</span>
        <ChevronDown class="w-3 h-3 transition-transform" :class="expanded ? 'rotate-180' : ''" />
      </span>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import { ChevronDown, Loader2 } from 'lucide-vue-next';
import type { UserTurn } from '@features/session/utils/userTurn';
import { phaseIcon } from '@features/session/utils/userTurn';

const props = defineProps<{ turn: UserTurn; expanded: boolean; readingWindow?: boolean }>();
const emit = defineEmits<{ toggle: [key: string] }>();
const icon = computed(() => phaseIcon(props.turn));
const summaryText = computed(() => String(props.turn.summary || ''));
</script>
