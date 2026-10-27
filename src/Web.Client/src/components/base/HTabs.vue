<!-- src/Web.Client/src/components/base/HTabs.vue -->
<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';

export interface TabItem {
  key: string;
  label: string;
  disabled?: boolean;
  /** 可选角标 */
  badge?: string | number;
}

/**
 * 标签页。
 *
 * ============ 键盘导航（WAI-ARIA tabs 模式） ============
 *  ← / →  切换（自动激活）
 *  Home / End  首 / 尾
 *  只有当前激活的 tab 的 tabindex=0，其他为 -1（roving tabindex）
 *  这样 Tab 键只停留在当前标签上，不会逐个走过所有标签
 * ============ 三种变体的用途 ============
 *  underline  页面级主导航（如 设置/会话/工具）
 *  pill       区块内的次级切换（如 消息/文件）
 *  card       并列视图切换（如 编辑/预览）
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    modelValue: string;
    items: TabItem[];
    variant?: 'underline' | 'pill' | 'card';
    /** 是否自动激活（切换焦点即激活） */
    autoActivate?: boolean;
  }>(),
  { variant: 'underline', autoActivate: true }
);

const emit = defineEmits<{ 'update:modelValue': [string] }>();

const containerRef = ref<HTMLElement | null>(null);

const enabledItems = computed(() => props.items.filter((i) => !i.disabled));

const containerClass = computed(() => {
  switch (props.variant) {
    case 'underline':
      return 'flex items-end gap-1 border-b border-border';
    case 'pill':
      return 'inline-flex items-center gap-1 rounded-lg bg-bg-muted p-1';
    case 'card':
      return 'flex items-center gap-2';
    default:
      return 'flex items-end gap-1 border-b border-border';
  }
});

function tabClass(item: TabItem): string[] {
  const active = item.key === props.modelValue;
  const base = [
    'relative inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap',
    'transition-colors duration-fast ease-out',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40',
    'disabled:cursor-not-allowed disabled:opacity-40',
  ];

  switch (props.variant) {
    case 'underline':
      return [
        ...base,
        'px-3 py-2 text-sm -mb-px border-b-2',
        active
          ? 'border-accent text-fg font-medium'
          : 'border-transparent text-fg-muted hover:text-fg hover:border-border-strong',
      ];
    case 'pill':
      return [
        ...base,
        'rounded-md px-2.5 py-1 text-sm',
        active ? 'bg-bg-elevated text-fg shadow-sm font-medium' : 'text-fg-muted hover:text-fg',
      ];
    case 'card':
      return [
        ...base,
        'rounded-md border px-3 py-1.5 text-sm',
        active
          ? 'border-accent bg-accent-subtle text-accent font-medium'
          : 'border-border bg-bg-elevated text-fg-muted hover:border-border-strong hover:text-fg',
      ];
  }
}

function select(item: TabItem) {
  if (item.disabled) return;
  emit('update:modelValue', item.key);
}

function focusAt(index: number) {
  const list = enabledItems.value;
  if (list.length === 0) return;
  const i = ((index % list.length) + list.length) % list.length;
  const key = list[i]!.key;
  const el = containerRef.value?.querySelector<HTMLElement>(`[data-key="${key}"]`);
  el?.focus();
  if (props.autoActivate) emit('update:modelValue', key);
}

function onKeydown(e: KeyboardEvent) {
  const list = enabledItems.value;
  const idx = list.findIndex((i) => i.key === props.modelValue);

  switch (e.key) {
    case 'ArrowRight':
      e.preventDefault();
      focusAt(idx + 1);
      break;
    case 'ArrowLeft':
      e.preventDefault();
      focusAt(idx - 1);
      break;
    case 'Home':
      e.preventDefault();
      focusAt(0);
      break;
    case 'End':
      e.preventDefault();
      focusAt(-1);
      break;
  }
}

/** modelValue 变化后，确保当前 tab 可见（横向滚动场景） */
watch(
  () => props.modelValue,
  async (v) => {
    await nextTick();
    const el = containerRef.value?.querySelector<HTMLElement>(`[data-key="${v}"]`);
    el?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
  }
);
</script>

<template>
  <div
    ref="containerRef"
    role="tablist"
    :class="containerClass"
    class="max-w-full overflow-x-auto scrollbar-none"
    @keydown="onKeydown"
  >
    <button
      v-for="item in items"
      :key="item.key"
      type="button"
      role="tab"
      :data-key="item.key"
      :aria-selected="item.key === modelValue"
      :aria-controls="`panel-${item.key}`"
      :tabindex="item.key === modelValue ? 0 : -1"
      :disabled="item.disabled"
      :class="tabClass(item)"
      @click="select(item)"
    >
      {{ item.label }}
      <span
        v-if="item.badge !== undefined"
        class="rounded-full bg-accent-subtle px-1.5 text-2xs font-medium text-accent"
      >
        {{ item.badge }}
      </span>
    </button>
  </div>
</template>
