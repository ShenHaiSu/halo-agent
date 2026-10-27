<!-- src/Web.Client/src/components/base/HRadioGroup.vue -->
<script setup lang="ts">
import { computed, provide, toRef } from 'vue';

/**
 * 单选组。
 *
 * ============ 为什么用 provide/inject 而不是逐个传 props ============
 *  组里有 N 个 HRadio，每个都要知道 name/modelValue/disabled。
 *  逐个传 props 会让模板冗长且容易漏。
 *  provide/inject 让 HRadio 只写自己的 value。
 * ============ 为什么用原生 input[type=radio] ============
 *  ① 方向键在组内切换（原生行为，自己实现要 30 行且容易错）
 *  ② 读屏会播报"1/3"
 *  ③ 只需要 name 相同，浏览器自动互斥
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    modelValue?: string | number | null;
    /** 原生 name，同组必须相同。不传则自动生成 */
    name?: string;
    disabled?: boolean;
    /** 纵向 / 横向排列 */
    direction?: 'vertical' | 'horizontal';
  }>(),
  { disabled: false, direction: 'vertical' }
);

const emit = defineEmits<{ 'update:modelValue': [string | number]; change: [string | number] }>();

const groupName = computed(() => props.name ?? `h-radio-${Math.random().toString(36).slice(2, 9)}`);

provide('hRadioGroup', {
  name: groupName,
  modelValue: toRef(props, 'modelValue'),
  disabled: toRef(props, 'disabled'),
  select(v: string | number) {
    emit('update:modelValue', v);
    emit('change', v);
  },
});
</script>

<template>
  <div
    role="radiogroup"
    :class="
      direction === 'horizontal' ? 'flex flex-wrap items-center gap-4' : 'flex flex-col gap-2'
    "
  >
    <slot />
  </div>
</template>
