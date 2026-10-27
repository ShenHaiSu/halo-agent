<!-- src/Web.Client/src/components/input/SlashPopover.vue -->
<script setup lang="ts">
import { computed } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { SlashCommand } from './useSlash';

const props = defineProps<{ items: SlashCommand[]; active: number }>();
defineEmits<{ select: []; hover: [index: number] }>();

const activeCmd = computed(() => props.items[props.active]);
const hintText = computed(() => `↑↓ 选择 · Enter ${activeCmd.value?.needsArgs ? '填入' : '执行'}`);
</script>

<template>
  <div class="slash" role="listbox" aria-label="命令">
    <div class="slash-head">
      <HaloIcon name="slash" :size="13" />
      <span>命令</span>
      <span class="spacer" />
      <span class="hint">{{ hintText }}</span>
    </div>
    <ul class="slash-list">
      <li
        v-for="(c, i) in items"
        :key="c.name"
        class="slash-item"
        :class="{ active: i === active }"
        role="option"
        :aria-selected="i === active"
        @mouseenter="$emit('hover', i)"
        @mousedown.prevent="$emit('select')"
      >
        <span class="sc-name mono">/{{ c.name }}</span>
        <span class="sc-desc">{{ c.desc }}</span>
        <span v-if="c.needsArgs" class="sc-args">需参数</span>
        <HaloIcon v-else name="arrow-right" :size="12" class="sc-run" />
      </li>
    </ul>
  </div>
</template>

<style scoped>
.slash {
  position: absolute;
  bottom: calc(100% - 8px);
  left: var(--space-4);
  z-index: var(--z-popover);
  width: 380px;
  max-width: calc(100vw - 32px);
  background: hsl(var(--bg-elevated));
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  overflow: hidden;
}
.slash-head {
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
.slash-list {
  list-style: none;
  margin: 0;
  padding: var(--space-1) 0;
  max-height: 300px;
  overflow-y: auto;
}
.slash-item {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: 3px var(--space-3);
  cursor: pointer;
  font-size: var(--text-xs);
  min-width: 0;
}
.slash-item.active {
  background: hsl(var(--bg-muted));
}
.mono {
  font-family: var(--font-mono);
}
.sc-name {
  color: hsl(var(--accent));
  flex: none;
}
.sc-desc {
  flex: 1;
  min-width: 0;
  color: hsl(var(--fg-muted));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.sc-args {
  color: hsl(var(--fg-subtle));
  font-size: var(--text-2xs);
  flex: none;
}
.sc-run {
  color: hsl(var(--fg-subtle));
  flex: none;
}
</style>
