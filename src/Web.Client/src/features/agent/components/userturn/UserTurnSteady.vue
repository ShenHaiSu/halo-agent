<template>
  <div
    v-if="lastContent"
    class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
    :class="isFlat ? '' : 'turn-final-slot'"
    :data-testid="isFlat ? 'user-turn-single' : 'turn-final'"
    @click="emit('link-click', $event)"
    @contextmenu.prevent="emit('assistant-menu', $event)"
    v-html="renderHtml(lastContent)"
  />
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { timelineViewFallback, useTimelineViewOptional } from '@features/agent/composables/timelineViewKey';

/** 稳态正文：单步直出与多步末段共用同一渲染（全文 Markdown，无汇总行）。 */
defineProps<{ lastContent: string; isFlat: boolean }>();

const emit = defineEmits<{ 'link-click': [e: MouseEvent]; 'assistant-menu': [e: MouseEvent] }>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());

function renderHtml(t: string): string {
  try {
    return view.value.renderHtml(t);
  } catch {
    return String(t || '');
  }
}
</script>
