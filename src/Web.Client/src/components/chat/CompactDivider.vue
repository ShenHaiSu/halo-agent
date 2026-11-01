<script setup lang="ts">
import { ref } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { CompactDividerView } from '@/contract/agent';

defineProps<{
  divider: CompactDividerView;
}>();

const isOpen = ref(false);

function formatTime(ts: number): string {
  if (!ts) return '';
  const d = new Date(ts);
  const h = d.getHours().toString().padStart(2, '0');
  const m = d.getMinutes().toString().padStart(2, '0');
  const s = d.getSeconds().toString().padStart(2, '0');
  return `${h}:${m}:${s}`;
}
</script>

<template>
  <div class="compact-divider-wrap" role="separator" :aria-label="'第' + divider.seq + '次上下文压缩'">
    <div class="divider-line" />

    <button
      type="button"
      class="divider-badge"
      :class="{ expanded: isOpen }"
      @click="isOpen = !isOpen"
    >
      <HaloIcon name="history" :size="13" class="badge-icon" />
      <span class="badge-title">第 {{ divider.seq }} 次上下文压缩</span>
      <span class="badge-meta">折叠 {{ divider.removedTurns }} 轮 · 保留 {{ divider.keptTurns }} 轮</span>
      <HaloIcon :name="isOpen ? 'chevron-up' : 'chevron-down'" :size="12" class="toggle-icon" />
    </button>

    <div class="divider-line" />
  </div>

  <div v-if="isOpen" class="divider-detail-card">
    <div class="detail-header">
      <span class="detail-title">此前会话要点浓缩</span>
      <span class="detail-time">{{ formatTime(divider.tsMs) }}</span>
    </div>
    <div class="detail-body markdown-body">
      {{ divider.summary }}
    </div>
  </div>
</template>

<style scoped>
.compact-divider-wrap {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin: var(--space-3) 0;
  width: 100%;
}

.divider-line {
  height: 1px;
  background: hsl(var(--border) / 0.8);
  flex: 1;
}

.divider-badge {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  padding: 4px 12px;
  background: hsl(var(--bg-subtle));
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-full);
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  cursor: pointer;
  transition: background 0.15s, color 0.15s, border-color 0.15s;
  user-select: none;
}

.divider-badge:hover {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg));
  border-color: hsl(var(--border));
}

.divider-badge.expanded {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg));
}

.badge-icon {
  flex-shrink: 0;
  color: hsl(var(--accent));
}

.badge-title {
  font-weight: 500;
  color: hsl(var(--fg));
}

.badge-meta {
  color: hsl(var(--fg-subtle));
}

.toggle-icon {
  flex-shrink: 0;
  margin-left: 2px;
}

.divider-detail-card {
  background: hsl(var(--bg-subtle));
  border: 1px dashed hsl(var(--border));
  margin: var(--space-1) var(--space-4) var(--space-3);
  padding: var(--space-3);
  border-radius: var(--radius-sm);
}

.detail-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: var(--space-2);
  font-size: var(--text-xs);
}

.detail-title {
  font-weight: 500;
  color: hsl(var(--fg));
}

.detail-time {
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}

.detail-body {
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
  white-space: pre-wrap;
  line-height: 1.6;
}
</style>
