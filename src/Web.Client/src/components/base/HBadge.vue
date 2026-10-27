<!-- src/Web.Client/src/components/base/HBadge.vue -->
<script setup lang="ts">
import { computed } from 'vue';

/**
 * 角标。
 *
 * ============ 三种形态 ============
 *  dot    纯圆点（表示"有新内容"）
 *  count  数字（超过 max 显示 "99+"）
 *  text   任意短文本
 * ============ 为什么 dot 要单独一种 ============
 *  数字角标需要宽度自适应（10 vs 999），圆点固定 6px。
 *  混在一起会让圆点也变成"6px 高的文字框"，视觉不对。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    content?: string | number;
    dot?: boolean;
    max?: number;
    /** 语义色 */
    variant?: 'default' | 'accent' | 'success' | 'warning' | 'danger';
    /** 内联（跟在文字后面） / 叠加（absolute 定位于父元素右上角） */
    mode?: 'inline' | 'overlay';
  }>(),
  { max: 99, variant: 'danger', mode: 'inline' }
);

const display = computed(() => {
  if (props.dot) return '';
  if (props.content === undefined || props.content === '') return '';
  const n = Number(props.content);
  if (!Number.isNaN(n)) return n > props.max ? `${props.max}+` : String(n);
  return String(props.content);
});

const variantClass = computed(() => {
  switch (props.variant) {
    case 'default':
      return 'bg-bg-muted text-fg-muted';
    case 'accent':
      return 'bg-accent text-accent-fg';
    case 'success':
      return 'bg-success text-white';
    case 'warning':
      return 'bg-warning text-white';
    case 'danger':
      return 'bg-danger text-white';
    default:
      return 'bg-bg-muted text-fg-muted';
  }
});
</script>

<template>
  <span
    v-if="dot"
    class="inline-block h-1.5 w-1.5 rounded-full"
    :class="[variantClass, mode === 'overlay' ? 'absolute right-0 top-0 ring-2 ring-bg' : '']"
    aria-hidden="true"
  />
  <span
    v-else-if="display"
    class="inline-flex items-center justify-center rounded-full px-1.5 text-2xs font-medium leading-4"
    :class="[variantClass, mode === 'overlay' ? 'absolute -right-1.5 -top-1.5 ring-2 ring-bg' : '']"
  >
    {{ display }}
  </span>
</template>
