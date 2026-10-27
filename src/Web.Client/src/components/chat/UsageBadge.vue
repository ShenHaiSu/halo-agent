<!-- src/Web.Client/src/components/chat/UsageBadge.vue -->
<!-- P5 用量徽标：显示 lastPromptTokens / contextWindow，dropped 非 0 时提示
     "因超出上下文丢弃了 N 条"。以 props 传入，由调用方接线 stores。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { formatNumber } from '@/utils/format';

const props = withDefaults(
  defineProps<{
    promptTokens?: number;
    contextWindow?: number;
    dropped?: number;
  }>(),
  { promptTokens: 0, contextWindow: 0, dropped: 0 }
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
    :title="dropped > 0 ? `因超出上下文丢弃了 ${dropped} 条消息` : '当前上下文用量'"
  >
    <span class="usage-text">{{ label }}</span>
    <span v-if="dropped > 0" class="usage-dropped">已丢弃 {{ dropped }} 条</span>
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
