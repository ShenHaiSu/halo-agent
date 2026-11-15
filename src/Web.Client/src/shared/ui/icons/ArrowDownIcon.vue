<template>
  <svg
    viewBox="0 0 24 24"
    :width="size"
    :height="size"
    fill="none"
    stroke="currentColor"
    :stroke-width="strokeWidth"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <!-- lucide ArrowDown 同构：竖线 + 直线单箭头 -->
    <path d="M12 5v14" />
    <path d="m19 12-7 7-7-7" />
  </svg>
</template>
<script setup lang="ts">
withDefaults(defineProps<{ size?: number | string; strokeWidth?: number | string }>(), {
  size: 18,
  strokeWidth: 2,
});
</script>
