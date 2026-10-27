<!-- src/Web.Client/src/components/assets/MemoryList.vue -->
<!-- 记忆列表（轻量转发：MemoryEditor 已含列表，直接复用）。 -->
<script setup lang="ts">
import MemoryEditor from './MemoryEditor.vue';

function isDirty(): boolean {
  return false;
}
defineExpose({ isDirty });
</script>

<template>
  <MemoryEditor />
</template>
