<!-- src/Web.Client/src/components/base/HDivider.vue -->
<script setup lang="ts">
/**
 * 分隔线（或带文字的分隔）。
 */
withDefaults(
  defineProps<{
    orientation?: 'horizontal' | 'vertical';
    /** 中间文字（仅 horizontal 有效） */
    label?: string;
  }>(),
  { orientation: 'horizontal' }
);
</script>

<template>
  <div
    v-if="orientation === 'vertical'"
    class="mx-2 inline-block h-full w-px bg-border align-middle"
    role="separator"
    aria-orientation="vertical"
  />
  <div
    v-else-if="label"
    class="flex items-center gap-3 py-2 text-2xs text-fg-subtle"
    role="separator"
  >
    <span class="h-px flex-1 bg-border" />
    <span class="shrink-0">{{ label }}</span>
    <span class="h-px flex-1 bg-border" />
  </div>
  <div v-else class="my-2 h-px w-full bg-border" role="separator" />
</template>
