<!-- src/Web.Client/src/components/input/DropOverlay.vue -->
<script setup lang="ts">
import HaloIcon from '@/components/icons/HaloIcon.vue';
</script>
<template>
  <div class="drop-overlay" aria-hidden="true">
    <div class="drop-inner">
      <HaloIcon name="upload" :size="28" />
      <span>松开以添加文件引用</span>
    </div>
  </div>
</template>
<style scoped>
.drop-overlay {
  position: absolute;
  inset: 0;
  z-index: var(--z-overlay);
  background: hsl(var(--bg) / 0.88);
  border: 2px dashed hsl(var(--accent));
  border-radius: var(--radius-md);
  display: flex;
  align-items: center;
  justify-content: center;
  pointer-events: none;
}
.drop-inner {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--space-2);
  color: hsl(var(--accent));
  font-size: var(--text-sm);
}
</style>
