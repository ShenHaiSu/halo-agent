<!-- src/Web.Client/src/components/base/HTag.vue -->
<script setup lang="ts">
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';

/**
 * 标签（用于分类、状态、筛选条件）。
 *
 * ============ 与 HBadge 的区别 ============
 *  Badge  = 附着在别的元素上的计数/提示（小、圆、无内容感）
 *  Tag    = 独立的内容块（有边框、可关闭、可带图标）
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    variant?: 'default' | 'accent' | 'success' | 'warning' | 'danger' | 'info';
    icon?: IconName;
    closable?: boolean;
    size?: 'sm' | 'md';
  }>(),
  { variant: 'default', closable: false, size: 'md' }
);

const emit = defineEmits<{ close: [] }>();

const variantClass: Record<string, string> = {
  default: 'border-border bg-bg-muted text-fg-muted',
  accent: 'border-accent/30 bg-accent-subtle text-accent',
  success: 'border-success/30 bg-success-subtle text-success',
  warning: 'border-warning/30 bg-warning-subtle text-warning',
  danger: 'border-danger/30 bg-danger-subtle text-danger',
  info: 'border-info/30 bg-info-subtle text-info',
};
</script>

<template>
  <span
    class="inline-flex items-center gap-1 rounded border font-medium"
    :class="[
      variantClass[props.variant],
      props.size === 'sm' ? 'px-1.5 py-0 text-2xs' : 'px-2 py-0.5 text-xs',
    ]"
  >
    <HaloIcon v-if="icon" :name="icon" :size="props.size === 'sm' ? 10 : 12" />
    <slot />
    <button
      v-if="closable"
      type="button"
      class="ml-0.5 rounded-sm opacity-60 hover:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-current"
      aria-label="移除"
      @click="emit('close')"
    >
      <HaloIcon name="x" :size="props.size === 'sm' ? 9 : 11" />
    </button>
  </span>
</template>
