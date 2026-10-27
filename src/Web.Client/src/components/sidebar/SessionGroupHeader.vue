<!-- src/Web.Client/src/components/sidebar/SessionGroupHeader.vue -->
<script setup lang="ts">
defineProps<{ label: string; height: number }>();
</script>
<template>
  <div class="group-head" :style="{ height: `${height}px` }" role="presentation">{{ label }}</div>
</template>
<style scoped>
.group-head {
  display: flex;
  align-items: center;
  padding: 0 var(--space-3);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-weight: 500;
  letter-spacing: 0.02em;
  position: sticky;
  top: 0;
  background: hsl(var(--bg));
  z-index: 1;
  box-sizing: border-box;
}
</style>
