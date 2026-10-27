<!-- src/Web.Client/src/components/base/HCheckbox.vue -->
<script setup lang="ts">
import { computed } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 复选框（含 indeterminate 三态）。
 *
 * ============ 为什么用 <input type="checkbox"> ============
 *  ① indeterminate 只能通过 JS 设到原生 input 上
 *  ② 原生 input 的键盘/读屏行为免费
 *  ③ 视觉上用 accent-color 或自绘都可，这里自绘（更可控）
 * ============ 三态的语义 ============
 *  checked=true                → 勾
 *  indeterminate=true          → 横线（部分选中）
 *  checked=false, indet=false  → 空
 *  注意：indeterminate 优先级高于 checked（HTML 规范）
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    modelValue?: boolean;
    indeterminate?: boolean;
    disabled?: boolean;
    label?: string;
    /** 只读：可聚焦但不可改 */
    readonly?: boolean;
  }>(),
  { modelValue: false, indeterminate: false, disabled: false, readonly: false }
);

const emit = defineEmits<{ 'update:modelValue': [boolean]; change: [boolean] }>();

const isDisabled = computed(() => props.disabled || props.readonly);

function onChange(e: Event) {
  const el = e.target as HTMLInputElement;
  const next = el.checked;
  emit('update:modelValue', next);
  emit('change', next);
}
</script>

<template>
  <label
    class="inline-flex cursor-pointer items-start gap-2 select-none"
    :class="isDisabled ? 'cursor-not-allowed opacity-60' : ''"
  >
    <span class="relative mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center">
      <input
        type="checkbox"
        class="peer absolute inset-0 h-full w-full cursor-inherit opacity-0"
        :checked="modelValue"
        :indeterminate="indeterminate"
        :disabled="isDisabled"
        @change="onChange"
      />
      <!-- 自绘方框 -->
      <span
        class="pointer-events-none flex h-4 w-4 items-center justify-center rounded border transition-colors duration-fast ease-out peer-focus-visible:ring-2 peer-focus-visible:ring-accent/40 peer-focus-visible:ring-offset-1 peer-focus-visible:ring-offset-bg"
        :class="
          modelValue || indeterminate
            ? 'border-accent bg-accent text-accent-fg'
            : 'border-border-strong bg-bg-elevated'
        "
      >
        <HaloIcon v-if="indeterminate" name="minus" :size="11" :stroke-width="3" />
        <HaloIcon v-else-if="modelValue" name="check" :size="11" :stroke-width="3" />
      </span>
    </span>

    <span v-if="label || $slots.default" class="text-sm text-fg">
      <slot>{{ label }}</slot>
    </span>
  </label>
</template>
