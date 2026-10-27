<!-- src/Web.Client/src/components/base/HEmpty.vue -->
<script setup lang="ts">
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';

/**
 * 空状态。
 *
 * ============ 空状态必须给出"下一步做什么" ============
 *  只写"暂无数据"是设计缺失。用户会想"那我该怎么办"。
 *  所以 description 与 action slot 都应该是"引导性"的。
 * ============ ============
 */
withDefaults(
  defineProps<{
    icon?: IconName;
    title?: string;
    description?: string;
    size?: 'sm' | 'md';
  }>(),
  { icon: 'empty' as IconName, size: 'md' }
);
</script>

<template>
  <div
    class="flex flex-col items-center justify-center gap-2 px-6 text-center"
    :class="size === 'sm' ? 'py-6' : 'py-12'"
  >
    <div
      class="flex items-center justify-center rounded-full bg-bg-muted text-fg-subtle"
      :class="size === 'sm' ? 'h-10 w-10' : 'h-14 w-14'"
    >
      <HaloIcon :name="icon" :size="size === 'sm' ? 18 : 24" />
    </div>

    <p v-if="title" class="font-medium text-fg" :class="size === 'sm' ? 'text-sm' : 'text-base'">
      {{ title }}
    </p>

    <p v-if="description" class="max-w-sm text-xs leading-relaxed text-fg-muted">
      {{ description }}
    </p>

    <div v-if="$slots.action" class="mt-1">
      <slot name="action" />
    </div>
  </div>
</template>
