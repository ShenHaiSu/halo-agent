<!-- src/Web.Client/src/components/base/HTextarea.vue -->
<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch, useAttrs } from 'vue';

/**
 * 多行输入框。
 *
 * ============ 自动高度的关键实现 ============
 *  ① 每次内容变化：先 `height = 'auto'`，再读 `scrollHeight`，再设回去
 *  ② 只有 `autoGrow` 为 true 时才做（固定高度更可控，多数场景不用自动）
 *  ③ 设上限 `maxHeight`，超过后出现内部滚动（不是无限长高）
 * ============ 为什么不用 CSS field-sizing ============
 *  `field-sizing: content` 需 Chrome 123+，109 不支持。
 * ============ ============
 */
defineOptions({ inheritAttrs: false });

const props = withDefaults(
  defineProps<{
    modelValue?: string;
    placeholder?: string;
    disabled?: boolean;
    readonly?: boolean;
    error?: boolean;
    rows?: number;
    maxlength?: number;
    /** 自动高度 */
    autoGrow?: boolean;
    minHeight?: number;
    maxHeight?: number;
  }>(),
  {
    disabled: false,
    readonly: false,
    error: false,
    rows: 3,
    autoGrow: false,
    minHeight: 0,
    maxHeight: 0,
  }
);

const emit = defineEmits<{
  'update:modelValue': [string];
  input: [string];
  enter: [KeyboardEvent];
  focus: [FocusEvent];
  blur: [FocusEvent];
}>();

const attrs = useAttrs();
const el = ref<HTMLTextAreaElement | null>(null);

const value = computed(() => props.modelValue ?? '');

const classes = computed(() => [
  'w-full min-w-0 resize-none rounded-md border bg-bg-elevated px-3 py-2 text-sm text-fg',
  'transition-colors duration-fast ease-out',
  'placeholder:text-fg-subtle',
  'hover:border-border-strong',
  'focus:outline-none focus:border-accent focus:ring-2 focus:ring-accent/25',
  'disabled:cursor-not-allowed disabled:bg-bg-muted disabled:text-fg-subtle',
  'read-only:bg-bg-subtle',
  props.error ? 'border-danger focus:border-danger focus:ring-danger/25' : 'border-border',
]);

const countState = computed(() => {
  if (!props.maxlength) return 'none' as const;
  const ratio = value.value.length / props.maxlength;
  if (ratio >= 1) return 'limit' as const;
  if (ratio >= 0.9) return 'near' as const;
  return 'normal' as const;
});

async function adjustHeight() {
  if (!props.autoGrow) return;
  await nextTick();
  const t = el.value;
  if (!t) return;

  t.style.height = 'auto';
  const min = props.minHeight || 0;
  const max = props.maxHeight || Infinity;
  const h = Math.min(Math.max(t.scrollHeight, min), max);
  t.style.height = `${h}px`;
  t.style.overflowY = t.scrollHeight > max ? 'auto' : 'hidden';
}

function onInput(e: Event) {
  const t = e.target as HTMLTextAreaElement;
  let v = t.value;
  if (props.maxlength && v.length > props.maxlength) {
    v = v.slice(0, props.maxlength);
    t.value = v;
  }
  emit('update:modelValue', v);
  emit('input', v);
  void adjustHeight();
}

function onKeydown(e: KeyboardEvent) {
  // Enter 不拦（多行输入需要换行）
  // Ctrl/Cmd+Enter 作为"发送"
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
    e.preventDefault();
    emit('enter', e);
  }
}

onMounted(() => void adjustHeight());
watch(value, () => void adjustHeight());
</script>

<template>
  <div class="w-full min-w-0">
    <textarea
      ref="el"
      v-bind="attrs"
      :class="classes"
      :value="value"
      :placeholder="placeholder"
      :disabled="disabled"
      :readonly="readonly"
      :rows="rows"
      :aria-invalid="error || undefined"
      :style="autoGrow ? { minHeight: minHeight ? `${minHeight}px` : undefined } : undefined"
      @input="onInput"
      @keydown="onKeydown"
      @focus="emit('focus', $event)"
      @blur="emit('blur', $event)"
    />

    <div
      v-if="maxlength"
      class="mt-1 flex justify-end text-2xs tabular-nums"
      :class="{
        'text-fg-subtle': countState === 'normal',
        'text-warning': countState === 'near',
        'text-danger': countState === 'limit',
      }"
      aria-live="polite"
    >
      {{ value.length }} / {{ maxlength }}
    </div>
  </div>
</template>
