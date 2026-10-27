<!-- src/Web.Client/src/components/base/HSwitch.vue -->
<script setup lang="ts">
import { computed } from 'vue';
import HSpinner from './HSpinner.vue';

/**
 * 开关。
 *
 * ============ 用 <button role="switch"> 而不是 <input type="checkbox"> ============
 *  ① 原生 checkbox 的样式在各平台上差异大，改造成本高
 *  ② role="switch" + aria-checked 是标准做法，读屏支持好
 *  ③ 键盘行为（空格切换）原生 button 已支持
 * ============ 为什么不需要 loading 态之外的"过渡动画"细节 ============
 *  滑块用 transform: translateX 移动，走 GPU 路径，不重排。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    modelValue?: boolean;
    disabled?: boolean;
    loading?: boolean;
    /** 无障碍标签（必填，因为开关旁边可能有描述文字） */
    label: string;
  }>(),
  { modelValue: false, disabled: false, loading: false }
);

const emit = defineEmits<{ 'update:modelValue': [boolean]; change: [boolean] }>();

const isDisabled = computed(() => props.disabled || props.loading);

function toggle() {
  if (isDisabled.value) return;
  const next = !props.modelValue;
  emit('update:modelValue', next);
  emit('change', next);
}
</script>

<template>
  <button
    type="button"
    role="switch"
    :aria-checked="modelValue"
    :aria-label="label"
    :disabled="isDisabled"
    class="relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors duration-fast ease-out focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40 focus-visible:ring-offset-1 focus-visible:ring-offset-bg disabled:cursor-not-allowed disabled:opacity-50"
    :class="modelValue ? 'bg-accent' : 'bg-border-strong'"
    @click="toggle"
  >
    <span
      class="pointer-events-none absolute flex h-4 w-4 items-center justify-center rounded-full bg-white shadow-sm transition-transform duration-fast ease-out"
      :class="modelValue ? 'translate-x-[18px]' : 'translate-x-0.5'"
    >
      <HSpinner v-if="loading" size="xs" class="!text-fg-subtle" />
    </span>
  </button>
</template>
