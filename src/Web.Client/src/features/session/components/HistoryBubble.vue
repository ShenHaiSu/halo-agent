<template>
  <button
    class="toc-bubble"
    :class="[`toc-bubble--${item.role}`, { 'is-active': active }]"
    :title="tooltipText"
    :aria-label="`跳转到第 ${item.index} 条：${item.label}`"
    :data-testid="`toc-bubble-${item.anchorKey}`"
    @click="$emit('jump', item)"
    @keydown.enter.prevent="$emit('jump', item)"
    @keydown.space.prevent="$emit('jump', item)"
  >
    <span class="toc-seq">{{ item.index }}</span>
    <span class="toc-label">{{ item.label }}</span>
    <span v-if="item.toolCount" class="toc-badge">🔧{{ item.toolCount }}</span>
  </button>
</template>
<script setup lang="ts">
/**
 * need19：单个气泡（左/右对齐、tooltip、活跃态）。
 * 不计算跳转坐标，只 emit jump。
 */
import { computed } from 'vue';
import type { TocItem } from '@features/session/utils/historyToc';
import { bubbleLabel } from '@features/session/utils/historyToc';

const props = defineProps<{
  item: TocItem;
  active?: boolean;
}>();
defineEmits<{
  (e: 'jump', item: TocItem): void;
}>();

const roleText = computed(() => (props.item.role === 'user' ? '用户' : props.item.role === 'assistant' ? '回复' : '系统'));
const tooltipText = computed(() => {
  const t = props.item;
  const preview = bubbleLabel(t.fullText, 60) || t.label;
  return `#${t.index} · ${roleText.value}\n${preview}\n共 ${t.charCount} 字`;
});
</script>
<style scoped>
.toc-bubble {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
  border: 1px solid transparent;
  cursor: pointer;
  text-align: left;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.toc-bubble:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 1px;
}
.toc-seq {
  font-size: 11px;
  font-family: var(--font-mono);
  color: var(--text-secondary);
  opacity: 0.7;
  flex: 0 0 auto;
}
.toc-label {
  overflow: hidden;
  text-overflow: ellipsis;
  flex: 1 1 auto;
}
.toc-badge {
  font-size: 11px;
  color: var(--text-secondary);
  flex: 0 0 auto;
}
</style>
