<!-- src/Web.Client/src/components/layout/DetailPane.vue -->
<script setup lang="ts">
import DetailTabs from '@/components/detail/DetailTabs.vue';

/**
 * 右抽屉内容：用量 / 工具 / 撤销 / 上下文 / MCP（P5 完整实现）。
 * 由 AppShell 以浮层抽屉承载，不挤压主区宽度。
 */
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <DetailTabs />
  </div>
</template>
