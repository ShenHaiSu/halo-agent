<!-- src/Web.Client/src/components/base/HButton.vue -->
<script setup lang="ts">
import { computed, useAttrs } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';
import HSpinner from './HSpinner.vue';

/**
 * 通用按钮。
 *
 * ============ 设计要点 ============
 *  ① 三种 size，四种 variant，五种状态
 *  ② loading 时自动 disabled，且保留宽度（避免布局抖动）
 *  ③ 图标在左（`icon`）或右（`iconRight`）
 *  ④ 透传原生属性（type / title / aria-* / data-*）
 *  ⑤ 用 <button> 而不是 <div role=button>：原生语义 + 原生键盘行为
 * ============ 为什么 loading 要保留宽度 ============
 *  如果 loading 时把文字换成 spinner，按钮宽度会突变，
 *  周围元素跳一下。所以 spinner 覆盖在文字上（文字 opacity:0），
 *  宽度由文字撑开。
 * ============ ============
 */
defineOptions({ inheritAttrs: false });

const props = withDefaults(
  defineProps<{
    variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
    size?: 'sm' | 'md' | 'lg';
    type?: 'button' | 'submit' | 'reset';
    loading?: boolean;
    disabled?: boolean;
    icon?: IconName;
    iconRight?: IconName;
    block?: boolean;
  }>(),
  {
    variant: 'secondary',
    size: 'md',
    type: 'button',
    loading: false,
    disabled: false,
    block: false,
  }
);

const emit = defineEmits<{ click: [e: MouseEvent] }>();

const attrs = useAttrs();

const isDisabled = computed(() => props.disabled || props.loading);

const base =
  'relative inline-flex items-center justify-center gap-2 ' +
  'rounded-md font-medium whitespace-nowrap ' +
  'transition-colors duration-fast ease-out ' +
  'focus-visible:outline-none focus-visible:ring-0 ' +
  'disabled:cursor-not-allowed select-none';

const sizes: Record<string, string> = {
  sm: 'h-7 px-2.5 text-xs',
  md: 'h-8 px-3 text-sm',
  lg: 'h-10 px-4 text-base',
};

const variants: Record<string, string> = {
  primary:
    'bg-accent text-accent-fg shadow-sm ' +
    'hover:bg-accent-hover ' +
    'active:bg-accent-hover ' +
    'disabled:bg-accent/50 disabled:text-accent-fg/70',
  secondary:
    'bg-bg-elevated text-fg border border-border shadow-sm ' +
    'hover:bg-bg-muted hover:border-border-strong ' +
    'active:bg-bg-muted ' +
    'disabled:bg-bg-muted disabled:text-fg-subtle disabled:border-border-subtle',
  ghost:
    'bg-transparent text-fg-muted ' +
    'hover:bg-bg-muted hover:text-fg ' +
    'active:bg-bg-muted ' +
    'disabled:text-fg-subtle disabled:bg-transparent',
  danger:
    'bg-danger text-white shadow-sm ' +
    'hover:brightness-95 ' +
    'active:brightness-90 ' +
    'disabled:bg-danger/50 disabled:text-white/70',
};

const classes = computed(() => [
  base,
  sizes[props.size],
  variants[props.variant],
  props.block ? 'w-full' : '',
]);

const iconSize = computed(() => (props.size === 'sm' ? 14 : props.size === 'lg' ? 18 : 16));

function onClick(e: MouseEvent) {
  if (isDisabled.value) {
    e.preventDefault();
    e.stopPropagation();
    return;
  }
  emit('click', e);
}
</script>

<template>
  <button
    v-bind="attrs"
    :type="type"
    :class="classes"
    :disabled="isDisabled"
    :aria-busy="loading || undefined"
    @click="onClick"
  >
    <!-- 图标（左） -->
    <HaloIcon v-if="icon && !loading" :name="icon" :size="iconSize" class="shrink-0" />

    <!-- 文字：loading 时透明但占位，避免宽度抖动 -->
    <span :class="loading ? 'opacity-0' : ''">
      <slot />
    </span>

    <!-- 图标（右） -->
    <HaloIcon v-if="iconRight && !loading" :name="iconRight" :size="iconSize" class="shrink-0" />

    <!-- loading：绝对定位覆盖，不参与布局 -->
    <span v-if="loading" class="absolute inset-0 flex items-center justify-center">
      <HSpinner size="sm" inherit-color />
    </span>
  </button>
</template>
