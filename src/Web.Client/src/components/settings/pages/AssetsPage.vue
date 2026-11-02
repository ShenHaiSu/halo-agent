<!-- src/Web.Client/src/components/settings/pages/AssetsPage.vue -->
<!-- 资源分页：技能 / 记忆 / 人格三 Tab（HTabs pill + 计数 badge），脏态聚合 expose；Tab 切换前自确认；Ctrl+S 页级统一分发。 -->
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { transport } from '@/transport';
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

/** 三项资源的条数（元数据预加载）。null 表示尚未返回或请求失败——徽章不显示数字，避免与真实的 0 混淆。 */
const preloaded = ref<{ skills: number | null; memory: number | null; persona: number | null }>({
  skills: null,
  memory: null,
  persona: null,
});

/** 计数取值：编辑器实时值 > 预加载快照 > 不渲染徽章。 */
function pick(editorCount: number | undefined, preloaded: number | null): number | undefined {
  if (editorCount !== undefined) return editorCount;
  if (preloaded !== null) return preloaded;
  return undefined;
}

const tabs = computed(() => [
  { key: 'skills', label: '技能', badge: pick(skills.value?.count, preloaded.value.skills) },
  { key: 'memory', label: '记忆', badge: pick(memory.value?.count, preloaded.value.memory) },
  { key: 'persona', label: '人格', badge: pick(persona.value?.count, preloaded.value.persona) },
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

function countOf(r: unknown): number | null {
  const items = (r as { items?: unknown } | null)?.items;
  return Array.isArray(items) ? items.length : null;
}

/**
 * 进入资源页即取三项条数：只读 list 的数组长度，不读正文。
 * allSettled 逐项结算，单项失败只让该项保持 null（徽章不显示数字），不污染其余两项。
 */
async function preloadCounts(): Promise<void> {
  const results = await Promise.allSettled([
    transport.request('asset.skills.list', null),
    transport.request('asset.memory.list', null),
    transport.request('asset.personas.list', null),
  ] as const);
  const [s, m, p] = results;
  if (s.status === 'fulfilled') preloaded.value.skills = countOf(s.value);
  if (m.status === 'fulfilled') preloaded.value.memory = countOf(m.value);
  if (p.status === 'fulfilled') preloaded.value.persona = countOf(p.value);
}

onMounted(() => {
  void preloadCounts();
});

// 编辑器挂载期间把实时条数回写为"最后已知值"：编辑器销毁后徽章回落时不至于滞后。
// 注意：这里必须判 undefined 而不是 ?? null——编辑器销毁时读到的是"未知"，不能当成 0。
watch(
  () => [skills.value?.count, memory.value?.count, persona.value?.count] as const,
  ([s, m, p]) => {
    if (s !== undefined) preloaded.value.skills = s;
    if (m !== undefined) preloaded.value.memory = m;
    if (p !== undefined) preloaded.value.persona = p;
  },
);

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
