<!-- src/Web.Client/src/components/chat/UsageBadge.vue -->
<!-- P5 用量徽标：显示 lastPromptTokens / contextWindow，dropped=true 时提示
     “因超出上下文有丢弃”。契约 usage.dropped 为 boolean（P13 清理 Number 混用）。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { formatNumber } from '@/utils/format';

const props = withDefaults(
  defineProps<{
    promptTokens?: number;
    contextWindow?: number;
    dropped?: boolean;
  }>(),
  { promptTokens: 0, contextWindow: 0, dropped: false }
);

const hasUsage = computed(() => props.contextWindow > 0);

const label = computed(() =>
  hasUsage.value
    ? `${formatNumber(props.promptTokens)} / ${formatNumber(props.contextWindow)}`
    : '用量未知'
);
</script>

<template>
  <span
    class="usage-badge"
    :title="dropped ? '因超出上下文丢弃了消息（responses 流）' : '当前上下文用量'"
  >
    <span class="usage-text">{{ label }}</span>
    <span v-if="dropped" class="usage-dropped">因超出上下文有丢弃</span>
  </span>
</template>

<style scoped>
.usage-badge {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  font-size: var(--text-2xs);
  font-family: var(--font-mono);
  color: hsl(var(--fg-subtle));
  min-width: 0;
}
.usage-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.usage-dropped {
  padding: 0 var(--space-1);
  border-radius: var(--radius-xs);
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
  white-space: nowrap;
}
</style>
