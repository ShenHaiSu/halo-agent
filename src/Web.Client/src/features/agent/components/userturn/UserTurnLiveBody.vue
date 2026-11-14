<template>
  <div class="turn-body rounded-lg border border-subtle bg-surface/40 px-3 py-2 min-w-0" data-testid="user-turn-live">
    <div v-for="(seg, i) in segs" :key="seg.key" :data-testid="`user-turn-live-seg-${i}`">
      <StreamSegItem
        :seg="seg"
        :stream-md-kill-switch="killSwitch"
        :render-stream="renderStream"
        :is-tail="i === segs.length - 1"
        :is-generating="isGenerating"
        @link-click="emit('link-click', $event)"
      />
    </div>
    <div v-if="!segs.length" class="flex items-center gap-2 text-xs text-secondary">
      <Loader2 class="w-3.5 h-3.5 animate-spin" />
      <span>Agent 推理中...</span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { Loader2 } from 'lucide-vue-next';
import StreamSegItem from '@features/agent/components/StreamSegItem.vue';
import { timelineViewFallback, useTimelineViewOptional, type TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';

/** 流态体：运行中全量展开，空段时仅保留一行推理占位。 */
defineProps<{ segs: TimelineLiveSeg[]; isGenerating: boolean }>();
const emit = defineEmits<{ 'link-click': [e: MouseEvent] }>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());
const killSwitch = computed(() => view.value.streamMdKillSwitch);

function renderStream(t: string): string {
  try {
    return view.value.renderStream(t);
  } catch {
    return String(t || '');
  }
}
</script>
