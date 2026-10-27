<!-- src/Web.Client/src/components/base/HIconButton.vue -->
<script setup lang="ts">
import { computed } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';

/**
 * 图标按钮。
 *
 * ============ 与 HButton 的区别 ============
 *  ① 只有图标，没有文字 → **必须**有 accessibility label
 *  ② 默认 ghost 变体（图标按钮通常是次要操作）
 *  ③ 支持 `pressed` 状态（用于切换按钮，如"显示侧栏"）
 * ============ label 是必需的 ============
 *  没有 label 的图标按钮对读屏用户是"按钮"两个字，
 *  完全不知道干什么。所以 label 是必填 prop（不是可选）。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    icon: IconName;
    /** 无障碍标签，同时作为 title 提示 */
    label: string;
    variant?: 'ghost' | 'subtle' | 'outline';
    size?: 'sm' | 'md' | 'lg';
    disabled?: boolean;
    /** 切换按钮的按下态 */
    pressed?: boolean;
    /** 是否显示为危险色（如删除） */
    danger?: boolean;
  }>(),
  { variant: 'ghost', size: 'md', disabled: false, pressed: false, danger: false }
);

const emit = defineEmits<{ click: [e: MouseEvent] }>();

const sizes: Record<string, { box: string; icon: number }> = {
  sm: { box: 'h-6 w-6', icon: 13 },
  md: { box: 'h-7 w-7', icon: 15 },
  lg: { box: 'h-8 w-8', icon: 17 },
};

const variants: Record<string, string> = {
  ghost: 'hover:bg-bg-muted',
  subtle: 'bg-bg-muted hover:bg-bg-subtle',
  outline: 'border border-border hover:bg-bg-muted hover:border-border-strong',
};

const classes = computed(() => [
  'inline-flex shrink-0 items-center justify-center rounded-md',
  'transition-colors duration-fast ease-out',
  'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40',
  'disabled:cursor-not-allowed disabled:opacity-40',
  sizes[props.size]!.box,
  variants[props.variant],
  props.pressed ? 'bg-accent-subtle text-accent' : 'text-fg-muted hover:text-fg',
  props.danger ? 'hover:bg-danger-subtle hover:text-danger' : '',
]);

const iconSize = computed(() => sizes[props.size]!.icon);

function onClick(e: MouseEvent) {
  if (props.disabled) {
    e.preventDefault();
    e.stopPropagation();
    return;
  }
  emit('click', e);
}
</script>

<template>
  <button
    type="button"
    :class="classes"
    :disabled="disabled"
    :title="label"
    :aria-label="label"
    :aria-pressed="pressed ? 'true' : undefined"
    @click="onClick"
  >
    <HaloIcon :name="icon" :size="iconSize" />
  </button>
</template>
