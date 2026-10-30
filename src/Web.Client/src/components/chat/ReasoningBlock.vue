<!-- src/Web.Client/src/components/chat/ReasoningBlock.vue -->
<!-- P5 D5-3 思考内容折叠块：默认折叠；展开时只渲染前 8000 字符 + 显示剩余（见 03 §6.1）。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

const props = defineProps<{ text: string; live: boolean; kind?: 'full' | 'summary' }>();

const open = ref(false);

// 思考内容可能几万字，展开后一次性渲染会让 Win7 卡顿：只渲染前 8000 字符
const PREVIEW_CHARS = 8000;
const expandedFull = ref(false);
const shown = computed(() =>
  props.text.length > PREVIEW_CHARS && !expandedFull.value
    ? props.text.slice(0, PREVIEW_CHARS)
    : props.text
);
const rest = computed(() => Math.max(0, props.text.length - PREVIEW_CHARS));
const hasMore = computed(() => props.text.length > PREVIEW_CHARS && !expandedFull.value);

const meta = computed(() =>
  props.live ? `思考中…（${props.text.length} 字）` : `共 ${props.text.length} 字`
);
</script>

<template>
  <div class="reasoning">
    <button
      class="reasoning-head"
      type="button"
      :aria-expanded="open"
      aria-label="思考过程"
      @click="open = !open"
    >
      <HaloIcon :name="open ? 'chevron-down' : 'chevron-right'" :size="14" />
      <span class="reasoning-title">{{ kind === 'summary' ? '思考摘要' : '思考过程' }}</span>
      <span class="reasoning-meta">{{ meta }}</span>
    </button>
    <div v-if="open" class="reasoning-body">
      {{ shown
      }}<template v-if="hasMore"
        >…（还有 {{ rest }} 字，<button
          type="button"
          class="reasoning-more"
          @click="expandedFull = true"
        >
          显示剩余</button
        >）</template
      >
    </div>
  </div>
</template>

<style scoped>
.reasoning {
  border: 1px dashed hsl(var(--border));
  border-radius: var(--radius-sm);
  background: hsl(var(--bg-subtle));
  overflow: hidden;
}
.reasoning-head {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  width: 100%;
  padding: var(--space-1) var(--space-2);
  background: transparent;
  border: 0;
  color: hsl(var(--fg-muted));
  font-size: var(--text-xs);
  cursor: pointer;
  text-align: left;
}
.reasoning-head:hover {
  background: hsl(var(--bg-muted));
}
.reasoning-title {
  font-weight: 500;
}
.reasoning-meta {
  color: hsl(var(--fg-subtle));
  font-size: var(--text-2xs);
}
.reasoning-body {
  padding: var(--space-2) var(--space-3);
  border-top: 1px dashed hsl(var(--border));
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
  white-space: pre-wrap;
  max-height: 320px;
  overflow-y: auto;
  overflow-wrap: anywhere;
}
.reasoning-more {
  background: transparent;
  border: 0;
  padding: 0;
  color: hsl(var(--accent));
  font-size: inherit;
  cursor: pointer;
  text-decoration: underline;
  text-underline-offset: 2px;
}
</style>
