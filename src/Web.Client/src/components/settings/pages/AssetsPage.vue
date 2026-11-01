<!-- src/Web.Client/src/components/settings/pages/AssetsPage.vue -->
<!-- 资源分页：技能 / 记忆 / 人格三 Tab（HTabs pill + 计数 badge），脏态聚合 expose；Tab 切换前自确认；Ctrl+S 页级统一分发。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import HTabs from '@/components/base/HTabs.vue';
import HButton from '@/components/base/HButton.vue';
import HDialog from '@/components/base/HDialog.vue';
import SkillEditor from '@/components/assets/SkillEditor.vue';
import MemoryEditor from '@/components/assets/MemoryEditor.vue';
import PersonaEditor from '@/components/assets/PersonaEditor.vue';

interface AssetChild {
  isDirty: () => boolean;
  reload: () => void;
  saveIfDirty: () => void;
  count?: number;
}

const tab = ref('skills');

const skills = ref<(AssetChild & { count?: number }) | null>(null);
const memory = ref<(AssetChild & { count?: number }) | null>(null);
const persona = ref<(AssetChild & { count?: number }) | null>(null);

const skillsCount = computed(() => skills.value?.count ?? 0);
const memoryCount = computed(() => memory.value?.count ?? 0);
const personaCount = computed(() => persona.value?.count ?? 0);

const tabs = computed(() => [
  { key: 'skills', label: '技能', badge: skillsCount.value },
  { key: 'memory', label: '记忆', badge: memoryCount.value },
  { key: 'persona', label: '人格', badge: personaCount.value },
]);

const activeChild = computed(() => {
  if (tab.value === 'skills') return skills.value;
  if (tab.value === 'memory') return memory.value;
  return persona.value;
});

const dirty = computed(() => {
  // 聚合三页脏态（null-safe，未挂载按干净计），切页/关页统一拦截
  return (
    skills.value?.isDirty() === true ||
    memory.value?.isDirty() === true ||
    persona.value?.isDirty() === true
  );
});

/** Tab 切换前自确认（SettingsApp 的 DirtyGuard 只拦跨分页/关页，不管页内 Tab）。 */
const pendingTab = ref<string | null>(null);
function requestTab(next: string): void {
  if (next === tab.value) return;
  if (dirty.value) {
    pendingTab.value = next;
    return;
  }
  tab.value = next;
}
function confirmTabSwitch(): void {
  if (pendingTab.value !== null) tab.value = pendingTab.value;
  pendingTab.value = null;
}
function cancelTabSwitch(): void {
  pendingTab.value = null;
}

/** Ctrl+S 页级统一分发：单一 preventDefault，按当前 Tab 存。 */
function onKeydown(e: KeyboardEvent): void {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
    e.preventDefault();
    activeChild.value?.saveIfDirty();
  }
}

function isDirty(): boolean {
  return dirty.value;
}
function resetToSaved(): void {
  // 资源编辑器由各自保存按钮提交；放弃时保持现状（DirtyGuard 拦截在设置页层）。
}
defineExpose({ isDirty, resetToSaved });
</script>

<template>
  <div class="assets-page" @keydown="onKeydown">
    <h2 class="page-title">资源</h2>
    <p class="page-desc">技能 / 记忆 / 人格的统一管理。改动即时落盘；切换页签或关闭设置前有未保存改动会先确认。</p>
    <HTabs :model-value="tab" :items="tabs" variant="pill" @update:model-value="requestTab" />
     <div
       :id="`panel-${tab}`"
       class="tab-body"
       role="tabpanel"
       :aria-label="tab === 'skills' ? '技能' : tab === 'memory' ? '记忆' : '人格'"
       tabindex="0"
     >
      <SkillEditor v-if="tab === 'skills'" ref="skills" />
      <MemoryEditor v-else-if="tab === 'memory'" ref="memory" />
      <PersonaEditor v-else ref="persona" />
    </div>

    <HDialog
      :open="pendingTab !== null"
      title="切换页签？"
      width="sm"
      @update:open="cancelTabSwitch()"
    >
      <p class="dlg-p">当前资源有未保存的改动，切换后可能丢失（切回也无法恢复）。</p>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="cancelTabSwitch">继续编辑</HButton>
        <HButton variant="danger" size="sm" @click="confirmTabSwitch">放弃改动并切换</HButton>
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.assets-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  gap: var(--space-2);
  max-width: none;
}
.page-title {
  font-size: var(--text-base);
  font-weight: 600;
  margin: 0;
}
.page-desc {
  margin: 0;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  line-height: 1.6;
}
.tab-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.tab-body:focus-visible {
  outline: none;
}
.dlg-p {
  margin: 0;
  font-size: var(--text-sm);
  line-height: 1.6;
}
</style>
