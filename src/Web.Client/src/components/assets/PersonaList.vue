<!-- src/Web.Client/src/components/assets/PersonaList.vue -->
<!-- 人格列表（轻量转发：PersonaEditor 已含列表，直接复用）。 -->
<script setup lang="ts">
import PersonaEditor from './PersonaEditor.vue';

function isDirty(): boolean {
  return false;
}
defineExpose({ isDirty });
</script>

<template>
  <PersonaEditor />
</template>
