<!-- src/Web.Client/src/components/input/MentionPopover.vue -->
<script setup lang="ts">
import { computed } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import type { MentionItem } from './useMention';

const props = defineProps<{
  items: MentionItem[];
  active: number;
  loading: boolean;
  error: string | null;
  empty: boolean;
}>();
defineEmits<{
  select: [];
  hover: [index: number];
}>();

const showEmpty = computed(() => props.empty && !props.loading && !props.error);
</script>

<template>
  <div class="mention" data-mention-popover role="listbox" aria-label="引用文件">
    <div class="mention-head">
      <HaloIcon name="at" :size="13" />
      <span>引用文件</span>
      <span class="spacer" />
      <span class="hint">↑↓ 选择 · Enter 确认 · Esc 关闭</span>
    </div>

    <div v-if="loading" class="mention-state">
      <HSpinner size="xs" />
      <span>正在读取目录…</span>
    </div>

    <div v-else-if="error" class="mention-state err">
      <HaloIcon name="alert" :size="13" />
      <span>{{ error }}</span>
      <span class="state-hint">可直接输入路径</span>
    </div>

    <div v-else-if="showEmpty" class="mention-state">
      <span>没有匹配的文件</span>
    </div>

    <ul v-else class="mention-list">
      <li
        v-for="(it, i) in items"
        :key="it.path"
        class="mention-item"
        :class="{ active: i === active }"
        role="option"
        :aria-selected="i === active"
        @mouseenter="$emit('hover', i)"
        @mousedown.prevent="$emit('select')"
      >
        <HaloIcon :name="it.isDir ? 'folder' : 'file-text'" :size="13" />
        <span class="mp-path">{{ it.path }}</span>
        <span v-if="it.isDir" class="mp-dir">目录</span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.mention {
  position: absolute;
  bottom: calc(100% - 8px);
  left: var(--space-4);
  z-index: var(--z-popover);
  width: 420px;
  max-width: calc(100vw - 32px);
  background: hsl(var(--bg-elevated));
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  overflow: hidden;
}
.mention-head {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  padding: var(--space-1) var(--space-2);
  border-bottom: 1px solid hsl(var(--border));
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
.spacer {
  flex: 1;
}
.hint {
  font-family: var(--font-mono);
}
.mention-state {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.mention-state.err {
  color: hsl(var(--danger));
}
.state-hint {
  margin-left: auto;
  color: hsl(var(--fg-subtle));
}
.mention-list {
  list-style: none;
  margin: 0;
  padding: var(--space-1) 0;
  max-height: 260px;
  overflow-y: auto;
}
.mention-item {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: 3px var(--space-3);
  cursor: pointer;
  font-size: var(--text-xs);
  color: hsl(var(--fg));
  min-width: 0;
}
.mention-item.active {
  background: hsl(var(--bg-muted));
}
.mp-path {
  flex: 1;
  min-width: 0;
  font-family: var(--font-mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.mp-dir {
  color: hsl(var(--fg-subtle));
  font-size: var(--text-2xs);
}
</style>
