<!-- src/Web.Client/src/components/chat/CompactionCard.vue -->
<!-- P18 上下文压缩分隔栏：标题行追加 移除 N 条·保留 M 轮；summary 为空不挂载（由 MessageList v-if 控制）。 -->
<script setup lang="ts">
import { ref } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

withDefaults(
  defineProps<{
    summary: string;
    /** 保留的消息（可选，显示前 KEEP_PREVIEW 条） */
    kept?: string[];
    /** P18：移除条数（可选） */
    removed?: number | null;
    /** P18：保留轮数（可选） */
    keptCount?: number | null;
  }>(),
  { kept: () => [], removed: null, keptCount: null }
);

const open = ref(false);
const KEEP_PREVIEW = 5;
</script>

<template>
  <div class="compact-card">
    <button
      class="cc-head"
      type="button"
      :aria-expanded="open"
      aria-label="上下文压缩摘要"
      @click="open = !open"
    >
      <HaloIcon name="history" :size="14" />
      <span class="cc-title">上下文已压缩</span>
      <span v-if="removed !== null && removed !== undefined" class="cc-meta"
        >移除 {{ removed }} 条<span v-if="keptCount !== null && keptCount !== undefined"> · 保留 {{ keptCount }} 轮</span
        ><span v-else> · 以上历史已折叠为摘要</span></span
      >
      <span v-else class="cc-meta">更早的消息已折叠为摘要</span>
      <span class="cc-spacer" />
      <HaloIcon :name="open ? 'chevron-up' : 'chevron-down'" :size="14" />
    </button>
    <div v-if="open" class="cc-body">{{ summary }}</div>
    <div v-if="open && kept.length > 0" class="cc-kept">
      <span class="cc-kept-label">保留的消息：</span>
      <span v-for="(k, i) in kept.slice(0, KEEP_PREVIEW)" :key="i" class="cc-kept-item">
        {{ k }}
      </span>
      <span v-if="kept.length > KEEP_PREVIEW" class="cc-kept-more"> …等 {{ kept.length }} 条 </span>
    </div>
  </div>
</template>

<style scoped>
.compact-card {
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-sm);
  background: hsl(var(--bg-subtle));
  overflow: hidden;
}
.cc-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  padding: var(--space-2) var(--space-3);
  background: transparent;
  border: 0;
  cursor: pointer;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  text-align: left;
}
.cc-head:hover {
  background: hsl(var(--bg-muted));
}
.cc-title {
  font-weight: 500;
  color: hsl(var(--fg));
}
.cc-meta {
  color: hsl(var(--fg-subtle));
}
.cc-spacer {
  flex: 1;
}
.cc-body {
  padding: var(--space-2) var(--space-3);
  border-top: 1px solid hsl(var(--border));
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
  white-space: pre-wrap;
  max-height: 300px;
  overflow-y: auto;
}
.cc-kept {
  padding: var(--space-1) var(--space-3) var(--space-2);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-1);
}
.cc-kept-item {
  padding: 0 4px;
  border-radius: var(--radius-xs);
  background: hsl(var(--bg-muted));
  font-family: var(--font-mono);
}
</style>
