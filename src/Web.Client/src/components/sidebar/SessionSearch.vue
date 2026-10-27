<!-- src/Web.Client/src/components/sidebar/SessionSearch.vue -->
<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import HInput from '@/components/base/HInput.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import { debounce } from '@/utils/debounce';

/**
 * 会话搜索：HInput + Ctrl+K 聚焦 + debounce 200ms 本地过滤。
 * 只做本地过滤，不调 session.list（会读磁盘，500 条输入会卡）。
 */
const props = defineProps<{ modelValue: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: string] }>();

const inner = ref(props.modelValue);

watch(
  () => props.modelValue,
  (v) => {
    inner.value = v;
  }
);

const push = debounce((v: string) => emit('update:modelValue', v), 200);

function onInput(v: string): void {
  inner.value = v;
  push(v);
}

function focusInput(): void {
  const el = document.querySelector('[data-session-search] input');
  if (el instanceof HTMLElement) el.focus();
}

function onKeydown(e: KeyboardEvent): void {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
    e.preventDefault();
    focusInput();
  }
}

onMounted(() => document.addEventListener('keydown', onKeydown));
onBeforeUnmount(() => document.removeEventListener('keydown', onKeydown));

defineExpose({ focus: focusInput });
</script>

<template>
  <div class="search-wrap" data-session-search>
    <HInput
      :model-value="inner"
      type="search"
      size="sm"
      placeholder="搜索会话（Ctrl+K）"
      aria-label="搜索会话"
      clearable
      @input="onInput"
    >
      <template #prefix>
        <HaloIcon name="search" :size="13" />
      </template>
    </HInput>
  </div>
</template>

<style scoped>
.search-wrap {
  padding: var(--space-2) var(--space-2) var(--space-1);
}
</style>
