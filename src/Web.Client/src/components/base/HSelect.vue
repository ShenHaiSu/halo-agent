<!-- src/Web.Client/src/components/base/HSelect.vue -->
<script setup lang="ts">
import { computed, useAttrs } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 原生 select 的样式包装。
 *
 * ============ 为什么用原生 ============
 *  ① Win7 上原生下拉在系统层绘制，性能与一致性最好
 *  ② 键盘（上下/Home/End/首字母跳转）、读屏、长列表滚动全部免费
 *  ③ 自定义下拉要做完这些需要 ~400 行，且一定有疏漏
 *  ④ 我们只需要"外壳好看"，选项列表的外观由系统决定是可接受的
 * ============ 唯一的外观技巧 ============
 *  隐藏原生箭头（appearance: none），自己叠一个 chevron。
 *  这样在所有平台上看起来一致。
 * ============ ============
 */
defineOptions({ inheritAttrs: false });

const props = withDefaults(
  defineProps<{
    modelValue?: string | number | null;
    size?: 'sm' | 'md';
    disabled?: boolean;
    error?: boolean;
    placeholder?: string;
  }>(),
  { size: 'md', disabled: false, error: false }
);

const emit = defineEmits<{ 'update:modelValue': [string] }>();

const attrs = useAttrs();

const classes = computed(() => [
  'w-full appearance-none rounded-md border bg-bg-elevated text-fg',
  'pr-8 pl-3 transition-colors duration-fast ease-out',
  'hover:border-border-strong',
  'focus:outline-none focus:border-accent focus:ring-2 focus:ring-accent/25',
  props.size === 'sm' ? 'h-7 text-xs' : 'h-8 text-sm',
  props.error ? 'border-danger' : 'border-border',
  props.disabled ? 'cursor-not-allowed opacity-60' : '',
]);

function onChange(e: Event) {
  emit('update:modelValue', (e.target as HTMLSelectElement).value);
}
</script>

<template>
  <div class="relative inline-flex w-full">
    <select
      v-bind="attrs"
      :class="classes"
      :disabled="disabled"
      :value="modelValue ?? ''"
      @change="onChange"
    >
      <option v-if="placeholder" value="" disabled>{{ placeholder }}</option>
      <slot />
    </select>
    <HaloIcon
      name="chevron-down"
      :size="14"
      class="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-fg-subtle"
    />
  </div>
</template>
