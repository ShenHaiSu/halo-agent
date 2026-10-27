<!-- src/Web.Client/src/components/settings/pages/AssetsPage.vue -->
<!-- 资源分页：技能 / 记忆 / 人格三 Tab（HTabs 仅头，面板手写 v-if），dirty 汇总 expose。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import HTabs from '@/components/base/HTabs.vue';
import SkillEditor from '@/components/assets/SkillEditor.vue';
import MemoryEditor from '@/components/assets/MemoryEditor.vue';
import PersonaEditor from '@/components/assets/PersonaEditor.vue';

const tab = ref('skills');

const skills = ref<{ isDirty: () => boolean } | null>(null);
const memory = ref<{ isDirty: () => boolean } | null>(null);
const persona = ref<{ isDirty: () => boolean } | null>(null);

const tabs = [
  { key: 'skills', label: '技能' },
  { key: 'memory', label: '记忆' },
  { key: 'persona', label: '人格' },
];

const dirty = computed(() => {
  if (tab.value === 'skills') return skills.value?.isDirty() === true;
  if (tab.value === 'memory') return memory.value?.isDirty() === true;
  if (tab.value === 'persona') return persona.value?.isDirty() === true;
  return false;
});

function isDirty(): boolean {
  return dirty.value;
}
function resetToSaved(): void {
  // 资源编辑器由各自保存按钮提交；放弃时保持现状（DirtyGuard 拦截在设置页层）。
}
defineExpose({ isDirty, resetToSaved });
</script>

<template>
  <div class="assets-page">
    <h2 class="page-title">资源</h2>
    <HTabs v-model="tab" :items="tabs" variant="pill" />
    <div class="tab-body" role="tabpanel">
      <SkillEditor v-if="tab === 'skills'" ref="skills" />
      <MemoryEditor v-else-if="tab === 'memory'" ref="memory" />
      <PersonaEditor v-else ref="persona" />
    </div>
  </div>
</template>

<style scoped>
.assets-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  gap: var(--space-2);
}
.page-title {
  font-size: var(--text-lg);
  font-weight: 600;
  margin: 0;
}
.tab-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
</style>
