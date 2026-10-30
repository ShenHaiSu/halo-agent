<!-- src/Web.Client/src/components/base/HInput.vue -->
<script setup lang="ts">
import { computed, ref, useAttrs } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 单行输入框。
 *
 * ============ 六个必须处理的细节 ============
 *  ① error 状态：边框变红 + aria-invalid
 *  ② 前置/后置 slot（用于图标、单位、按钮）
 *  ③ clearable：有内容时显示清除按钮
 *  ④ maxlength 计数：接近上限时变色
 *  ⑤ disabled / readonly 的视觉区分
 *  ⑥ 需要 `min-w-0`（否则在 flex 容器里会被内容撑破）
 * ============ 为什么不用 v-model 而是 modelValue + emit ============
 *  Vue 3.4+ 的 defineModel 更简洁，但我们还要处理
 *  "清除按钮"与"计数"，需要自己控制 emit 时机。
 *  显式 emit 更清晰。
 * ============ ============
 */
defineOptions({ inheritAttrs: false });

const props = withDefaults(
  defineProps<{
    modelValue?: string | number;
    size?: 'sm' | 'md';
    type?: 'text' | 'password' | 'email' | 'number' | 'search' | 'url' | 'tel';
    placeholder?: string;
    disabled?: boolean;
    readonly?: boolean;
    error?: boolean;
    clearable?: boolean;
    maxlength?: number;
    /** 输入时实时过滤（返回 false 拒绝本次输入） */
    filter?: (value: string) => boolean;
  }>(),
  { size: 'md', type: 'text', disabled: false, readonly: false, error: false, clearable: false }
);

const emit = defineEmits<{
  'update:modelValue': [string];
  input: [string];
  enter: [];
  focus: [FocusEvent];
  blur: [FocusEvent];
}>();

const attrs = useAttrs();
const inputRef = ref<HTMLInputElement | null>(null);

const value = computed(() => (props.modelValue === undefined ? '' : String(props.modelValue)));

const showClear = computed(
  () => props.clearable && !props.disabled && !props.readonly && value.value.length > 0
);

const countState = computed(() => {
  if (!props.maxlength) return 'none' as const;
  const n = value.value.length;
  const ratio = n / props.maxlength;
  if (ratio >= 1) return 'limit' as const;
  if (ratio >= 0.9) return 'near' as const;
  return 'normal' as const;
});

 const classes = computed(() => [
   'w-full min-w-0 rounded-md border-2 bg-bg-elevated text-fg',
   'transition-colors duration-fast ease-out',
   'placeholder:text-fg-subtle',
   'disabled:cursor-not-allowed disabled:border-border-subtle disabled:bg-bg-muted disabled:text-fg-subtle disabled:opacity-60',
   'read-only:bg-bg-subtle',
   props.size === 'sm' ? 'h-7 px-2 text-xs' : 'h-8 px-3 text-sm',
   props.error
     ? 'border-danger hover:border-danger focus-visible:border-danger'
     : 'border-border-strong hover:border-fg-subtle focus-visible:border-accent',
 ]);

function onInput(e: Event) {
  const el = e.target as HTMLInputElement;
  let v = el.value;

  // 过滤
  if (props.filter && !props.filter(v)) {
    el.value = value.value;
    return;
  }
  // maxlength（原生已限制，这里兜底粘贴场景）
  if (props.maxlength && v.length > props.maxlength) {
    v = v.slice(0, props.maxlength);
    el.value = v;
  }

  emit('update:modelValue', v);
  emit('input', v);
}

function onClear() {
  emit('update:modelValue', '');
  emit('input', '');
  inputRef.value?.focus();
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter') {
    e.preventDefault();
    emit('enter');
  }
}
function focus(): void {
  inputRef.value?.focus();
}
defineExpose({ focus });
</script>

<template>
  <div class="relative flex w-full min-w-0 items-center">
    <!-- 前置 -->
    <div
      v-if="$slots.prefix"
      class="pointer-events-none absolute left-2.5 flex items-center text-fg-subtle"
    >
      <slot name="prefix" />
    </div>

    <input
      ref="inputRef"
      v-bind="attrs"
      :type="type"
      :class="[classes, $slots.prefix ? 'pl-8' : '', showClear ? 'pr-8' : '']"
      :value="value"
      :placeholder="placeholder"
      :disabled="disabled"
      :readonly="readonly"
      :maxlength="maxlength"
      :aria-invalid="error || undefined"
      @input="onInput"
      @keydown="onKeydown"
      @focus="emit('focus', $event)"
      @blur="emit('blur', $event)"
    />

    <!-- 清除 -->
    <button
      v-if="showClear"
      type="button"
      class="absolute right-1.5 flex h-5 w-5 items-center justify-center rounded text-fg-subtle hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
      aria-label="清除"
      @click="onClear"
    >
      <HaloIcon name="x" :size="12" />
    </button>

    <!-- 后置 -->
    <div v-if="$slots.suffix && !showClear" class="absolute right-2 flex items-center">
      <slot name="suffix" />
    </div>
  </div>

  <!-- 计数 -->
  <div
    v-if="maxlength"
    class="mt-1 text-right text-2xs tabular-nums"
    :class="{
      'text-fg-subtle': countState === 'normal',
      'text-warning': countState === 'near',
      'text-danger': countState === 'limit',
    }"
    aria-live="polite"
  >
    {{ value.length }} / {{ maxlength }}
  </div>
</template>
