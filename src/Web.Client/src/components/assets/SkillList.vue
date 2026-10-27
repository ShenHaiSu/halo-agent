<!-- src/Web.Client/src/components/assets/SkillList.vue -->
<!-- 技能列表（轻量转发：SkillEditor 已含列表，直接复用）。 -->
<script setup lang="ts">
import SkillEditor from './SkillEditor.vue';

function isDirty(): boolean {
  return false;
}
defineExpose({ isDirty });
</script>

<template>
  <SkillEditor />
</template>
