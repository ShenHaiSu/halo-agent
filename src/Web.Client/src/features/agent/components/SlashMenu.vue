<template>
  <div v-if="visible" class="slash-menu" data-testid="slash-menu" role="listbox" aria-label="斜杠候选">
    <div class="slash-menu-head">
      <span>候选</span>
      <span class="slash-menu-count">{{ candidates.length }}</span>
      <span v-if="source" class="slash-menu-source">来源：{{ source }}</span>
      <span v-if="loading" class="slash-menu-loading">加载中…</span>
      <span v-if="error" class="slash-menu-error" data-testid="slash-menu-error">{{ error }}</span>
      <button type="button" class="slash-menu-close" data-testid="slash-menu-close" aria-label="关闭候选面板" @click="emit('close')">
        ×
      </button>
    </div>
    <ul v-if="candidates.length" ref="listEl" class="slash-menu-list">
      <li
        v-for="(c, i) in candidates"
        :key="c.id"
        class="slash-menu-item"
        :class="{ active: i === index }"
        :data-testid="`slash-item-${c.id}`"
        role="option"
        :aria-selected="i === index"
        @mouseenter="emit('hover', i)"
        @click="emit('select', c)"
      >
        <span class="slash-scope" :class="c.scope">{{ c.prefix || '' }}</span>
        <span class="slash-label">{{ c.label }}</span>
        <span v-if="c.title" class="slash-title">{{ c.title }}</span>
      </li>
    </ul>
    <div v-else class="slash-menu-empty" data-testid="slash-menu-empty">无匹配项</div>
  </div>
</template>
<script setup lang="ts">
/**
 * need22-F03：输入框上方的斜杠候选面板（纯展示：props in / events out）。
 * 面板状态由 useSlashMenu 持有，本组件不改编辑器。
 */
import { computed, nextTick, ref, watch } from 'vue';
import type { SlashCandidate } from '@shared/utils/slashRegistry';

const props = defineProps<{
  visible: boolean;
  candidates: SlashCandidate[];
  index: number;
  error?: string;
  loading?: boolean;
}>();
const emit = defineEmits<{
  select: [candidate: SlashCandidate];
  hover: [index: number];
  close: [];
}>();
/** G2：面板头显示来源 provider（便于将来多 provider） */
const source = computed(() => props.candidates[0]?.providerId || '');
const listEl = ref<HTMLElement | null>(null);
/** G3：高亮项跟随滚动 */
watch(
  () => [props.index, props.visible, props.candidates.length] as const,
  async () => {
    await nextTick();
    const host = listEl.value;
    if (!host) return;
    const el = host.querySelector('.slash-menu-item.active');
    if (el && typeof (el as HTMLElement).scrollIntoView === 'function') {
      (el as HTMLElement).scrollIntoView({ block: 'nearest' });
    }
  }
);
</script>
<style>
.slash-menu {
  position: absolute;
  bottom: 100%;
  left: 0;
  right: 0;
  margin-bottom: 8px;
  z-index: 40;
  background: var(--bg-elevated, #1f2937);
  border: 1px solid var(--border-subtle, #374151);
  border-radius: 8px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
  overflow: hidden;
  font-size: 12px;
  color: var(--text-primary, #e5e7eb);
}
.slash-menu-head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  border-bottom: 1px solid var(--border-subtle, #374151);
  font-size: 11px;
  color: var(--text-secondary, #9ca3af);
}
.slash-menu-count {
  opacity: 0.8;
}
.slash-menu-source {
  opacity: 0.7;
}
.slash-menu-loading {
  opacity: 0.7;
}
.slash-menu-error {
  color: #f43f5e;
  max-width: 60%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.slash-menu-close {
  margin-left: auto;
  border: none;
  background: transparent;
  color: inherit;
  cursor: pointer;
  font-size: 13px;
  line-height: 1;
  padding: 0 2px;
}
.slash-menu-list {
  list-style: none;
  margin: 0;
  padding: 2px 0;
  max-height: 260px;
  overflow-y: auto;
  max-width: 100%;
}
.slash-menu-item {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  cursor: pointer;
  white-space: nowrap;
}
.slash-menu-item.active {
  background: rgba(59, 130, 246, 0.18);
}
.slash-scope {
  font-size: 10px;
  opacity: 0.85;
  text-transform: lowercase;
}
.slash-scope.global {
  color: #a78bfa;
}
.slash-scope.project {
  color: #38bdf8;
}
.slash-label {
  font-family: var(--font-mono, monospace);
}
.slash-title {
  opacity: 0.6;
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
}
.slash-menu-empty {
  padding: 10px 8px;
  text-align: center;
  color: var(--text-secondary, #9ca3af);
  font-size: 11px;
}
</style>
