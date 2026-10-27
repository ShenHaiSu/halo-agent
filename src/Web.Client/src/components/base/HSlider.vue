<!-- src/Web.Client/src/components/base/HSlider.vue -->
<script setup lang="ts">
import { computed, ref } from 'vue';

/**
 * 滑块。
 *
 * ============ 为什么用原生 input[type=range] + 自定义视觉 ============
 *  原生 range 的键盘（方向键 / Home / End / PageUp/Down）、
 *  触屏拖动、discrete 步进、读屏播报全部免费且正确。
 *  自己用 div 实现需要 ~200 行，且一定漏掉某些键盘行为。
 *
 *  视觉上：把原生 input 设为 opacity:0 覆盖在自绘轨道上方，
 *  这样交互全走原生，视觉全走自定义。
 * ============ 进度填充的实现 ============
 *  用 linear-gradient 按百分比填充，避免用伪元素改宽度（后者会触发重排）。
 *  实际上 gradient 改动也是重绘，但比改宽度轻。
 * ============ ============
 */
const props = withDefaults(
  defineProps<{
    modelValue?: number;
    min?: number;
    max?: number;
    step?: number;
    disabled?: boolean;
    /** 显示当前值（或自定义格式） */
    showValue?: boolean;
    formatValue?: (v: number) => string;
    label?: string;
  }>(),
  { modelValue: 0, min: 0, max: 100, step: 1, disabled: false, showValue: false }
);

const emit = defineEmits<{ 'update:modelValue': [number]; change: [number] }>();

const el = ref<HTMLInputElement | null>(null);

const percent = computed(() => {
  const range = props.max - props.min;
  if (range <= 0) return 0;
  return Math.min(100, Math.max(0, ((props.modelValue - props.min) / range) * 100));
});

const displayValue = computed(() =>
  props.formatValue ? props.formatValue(props.modelValue) : String(props.modelValue)
);

const trackStyle = computed(() => ({
  background: `linear-gradient(to right,
    hsl(var(--accent)) 0%,
    hsl(var(--accent)) ${percent.value}%,
    hsl(var(--border-strong)) ${percent.value}%,
    hsl(var(--border-strong)) 100%)`,
}));

function onInput(e: Event) {
  const v = Number((e.target as HTMLInputElement).value);
  emit('update:modelValue', v);
}

function onChange(e: Event) {
  const v = Number((e.target as HTMLInputElement).value);
  emit('change', v);
}
</script>

<template>
  <div class="w-full min-w-0">
    <div v-if="label || showValue" class="mb-1 flex items-center justify-between">
      <span v-if="label" class="text-xs text-fg-muted">{{ label }}</span>
      <span v-if="showValue" class="font-mono text-xs tabular-nums text-fg">{{
        displayValue
      }}</span>
    </div>

    <div class="relative flex h-5 items-center">
      <!-- 自绘轨道 -->
      <div class="h-1.5 w-full rounded-full" :style="trackStyle" />
      <!-- 自绘滑块（位置用 left 百分比，不用 transform，因为要跟随原生 thumb） -->
      <div
        class="pointer-events-none absolute h-3.5 w-3.5 -translate-x-1/2 rounded-full border-2 border-accent bg-bg-elevated shadow-sm"
        :style="{ left: `${percent}%` }"
      />
      <!-- 原生 input 覆盖（透明） -->
      <input
        ref="el"
        type="range"
        class="absolute inset-0 h-full w-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
        :min="min"
        :max="max"
        :step="step"
        :value="modelValue"
        :disabled="disabled"
        :aria-label="label"
        @input="onInput"
        @change="onChange"
      />
    </div>
  </div>
</template>
