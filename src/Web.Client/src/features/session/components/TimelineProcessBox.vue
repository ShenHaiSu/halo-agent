<template>
  <div class="timeline-turn min-w-0" :data-turn-key="turn.turnId" :data-testid="`timeline-turn-${turn.turnId}`">
    <div v-if="turn.userPreview" class="flex justify-end turn-user-slot">
      <div
        class="max-w-[85%] rounded-lg bg-elevated border border-subtle px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
      >
        {{ turn.userPreview }}
      </div>
    </div>
    <div
      class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 overflow-hidden mt-1"
      :class="turn.failed > 0 ? 'border-rose-500' : 'border-indigo-500/70'"
      data-testid="timeline-process-box"
    >
      <div
        class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none"
        :data-testid="`timeline-process-head-${turn.turnId}`"
        @click="toggle"
      >
        <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
          <span aria-hidden="true">{{ badge }}</span>
          <span class="truncate">过程内容</span>
          <span v-if="label"> · {{ label }}</span>
        </span>
        <span class="flex items-center gap-1 text-[11px] opacity-80 shrink-0">
          <span>{{ expanded ? '点击折叠' : '点击展开' }}</span>
        </span>
      </div>
    </div>
    <div
      v-if="expanded"
      class="mt-1 rounded-md border border-subtle bg-surface/40 px-3 py-2 min-w-0"
      :data-testid="`timeline-process-body-${turn.turnId}`"
    >
      <div class="text-[11px] text-secondary whitespace-pre-wrap break-words select-text">
        {{ previewText }}
      </div>
    </div>
    <div
      v-if="turn.finalPreview"
      class="mt-1 app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
      :data-testid="`timeline-final-${turn.turnId}`"
    >
      {{ turn.finalPreview }}
    </div>
    <div v-else class="mt-1 text-[12px] text-secondary" :data-testid="`timeline-final-empty-${turn.turnId}`">
      本轮无正文，{{ turn.tools }} 个工具
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import type { FlatTimelineTurn } from '@features/session/utils/timelineSummary';
import { processBoxLabel, statusBadge } from '@features/session/utils/timelineSummary';

const props = defineProps<{
  turn: FlatTimelineTurn;
}>();

const expanded = ref(false);
function toggle(): void {
  expanded.value = !expanded.value;
}

const badge = computed(() => statusBadge(props.turn.status));
const label = computed(() => processBoxLabel(props.turn));
const previewText = computed(() => {
  const text = String(props.turn.finalPreview || '').trim();
  if (text) return text;
  if (props.turn.tools > 0) return `共 ${props.turn.blocks} 个过程块，${props.turn.tools} 个工具调用，展开查看全文请升级到完整加载。`;
  return '暂无过程预览。';
});
</script>

<style scoped>
.timeline-turn {
  display: flex;
  flex-direction: column;
  content-visibility: auto;
  contain-intrinsic-size: auto 240px;
}
.turn-user-slot {
  order: 1;
}
</style>
