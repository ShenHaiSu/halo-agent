<!-- src/Web.Client/src/components/input/ModeSelectorPanel.vue -->
<!-- 权限级别快切面板：4 档列表 + listbox 键盘，仿 ModelSelectorPanel 的功能与样式。 -->
<!-- 点选即切换，无二次确认（与权限设置页的危险模式确认不同）。 -->
<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from 'vue';
import HButton from '@/components/base/HButton.vue';
import type { PermissionMode } from '@/stores/session';
import { PERMISSION_MODES } from './permissionModes';

const props = defineProps<{
  current: PermissionMode;
}>();

const emit = defineEmits<{
  select: [mode: PermissionMode];
  close: [];
  openSettings: [];
}>();

const activeIdx = ref(
  Math.max(
    0,
    PERMISSION_MODES.findIndex((m) => m.value === props.current)
  )
);
const listRef = ref<HTMLElement | null>(null);

watch(
  () => props.current,
  (v) => {
    const i = PERMISSION_MODES.findIndex((m) => m.value === v);
    if (i >= 0) activeIdx.value = i;
  }
);

function scrollActiveIntoView(): void {
  void nextTick(() => {
    const el = listRef.value?.querySelector(
      `[data-idx="${activeIdx.value}"]`
    ) as HTMLElement | null;
    el?.scrollIntoView?.({ block: 'nearest' });
  });
}

function onKeydown(e: KeyboardEvent): void {
  switch (e.key) {
    case 'ArrowDown':
      e.preventDefault();
      activeIdx.value = (activeIdx.value + 1) % PERMISSION_MODES.length;
      scrollActiveIntoView();
      break;
    case 'ArrowUp':
      e.preventDefault();
      activeIdx.value = (activeIdx.value - 1 + PERMISSION_MODES.length) % PERMISSION_MODES.length;
      scrollActiveIntoView();
      break;
    case 'Home':
      e.preventDefault();
      activeIdx.value = 0;
      scrollActiveIntoView();
      break;
    case 'End':
      e.preventDefault();
      activeIdx.value = PERMISSION_MODES.length - 1;
      scrollActiveIntoView();
      break;
    case 'Enter': {
      e.preventDefault();
      const m = PERMISSION_MODES[activeIdx.value];
      if (m) emit('select', m.value);
      break;
    }
    case 'Escape':
      e.preventDefault();
      e.stopPropagation();
      emit('close');
      break;
    case 'Tab':
      emit('close');
      break;
  }
}

defineExpose({
  focusFirst: () => {
    void nextTick(() => {
      const el = listRef.value?.querySelector('[role="option"]') as HTMLElement | null;
      el?.scrollIntoView?.({ block: 'nearest' });
    });
  },
});

onMounted(() => {
  scrollActiveIntoView();
});
</script>

<template>
  <div class="pm-panel" role="listbox" aria-label="权限级别" @keydown="onKeydown">
    <div ref="listRef" class="pm-list">
      <div
        v-for="(m, i) in PERMISSION_MODES"
        :key="m.value"
        :data-idx="i"
        role="option"
        :aria-selected="m.value === props.current"
        class="pm-row"
        :class="{ current: m.value === props.current, focused: i === activeIdx }"
        :title="`${m.label}：${m.desc}`"
        @click="emit('select', m.value)"
        @mouseenter="activeIdx = i"
      >
        <span v-if="m.value === props.current" class="pm-cur" aria-hidden="true">●</span>
        <span class="pm-dot" :class="{ danger: m.danger, warning: m.warning }" aria-hidden="true"
          >●</span
        >
        <span class="pm-main">{{ m.label }}</span>
        <span class="pm-desc">{{ m.desc }}</span>
      </div>
    </div>
    <div class="pm-foot">
      <HButton size="sm" variant="ghost" @click="emit('openSettings')">去权限设置</HButton>
    </div>
  </div>
</template>

<style scoped>
.pm-panel {
  min-width: 280px;
  width: 340px;
  max-width: calc(100vw - 32px);
  max-height: min(320px, 50vh);
  display: flex;
  flex-direction: column;
  background: hsl(var(--bg-elevated));
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  overflow: hidden;
}
.pm-list {
  overflow-y: auto;
  padding: var(--space-1);
  min-height: 0;
}
.pm-row {
  display: flex;
  align-items: center;
  gap: var(--space-1-5);
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-sm);
  cursor: pointer;
  min-width: 0;
}
.pm-row:hover,
.pm-row.focused {
  background: hsl(var(--bg-muted));
}
.pm-row.current {
  background: hsl(var(--accent-subtle));
}
.pm-cur {
  color: hsl(var(--accent));
  font-size: var(--text-2xs);
  flex-shrink: 0;
}
.pm-dot {
  color: hsl(var(--fg-subtle));
  font-size: var(--text-2xs);
  flex-shrink: 0;
}
.pm-dot.warning {
  color: hsl(var(--warning));
}
.pm-dot.danger {
  color: hsl(var(--danger));
}
.pm-main {
  font-size: var(--text-sm);
  font-weight: 600;
  color: hsl(var(--fg));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex-shrink: 0;
}
.pm-desc {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pm-foot {
  display: flex;
  justify-content: flex-end;
  padding: var(--space-1-5) var(--space-2);
  border-top: 1px solid hsl(var(--border-subtle));
}
</style>
