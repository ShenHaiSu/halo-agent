<!-- src/Web.Client/src/components/assets/SkillEditor.vue -->
<!-- 技能编辑器（05 §4.1 + §4.2 + §4.3 + §4.4）：draftCache 替代 confirm；entry 只读；builtin 只读+复制为新。 -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { SKILL_NAME_RE } from '@/components/settings/validators';
import AssetLayout from './AssetLayout.vue';
import AssetListItem from './AssetListItem.vue';
import HTextarea from '@/components/base/HTextarea.vue';
import HInput from '@/components/base/HInput.vue';
import HButton from '@/components/base/HButton.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HDialog from '@/components/base/HDialog.vue';

interface SkillSummary {
  name: string;
  body?: string;
  entry?: string;
  source?: 'builtin' | 'user';
}

const toast = useToast();

const items = ref<SkillSummary[]>([]);
const warnings = ref<string[]>([]);
const loading = ref(true);
const loadError = ref<string | null>(null);

const selectedName = ref<string | null>(null);
const current = ref<{ name: string; body: string; entry?: string; source?: string } | null>(null);

const draftBody = ref('');
const creating = ref(false);
const saving = ref(false);
const confirmDelete = ref(false);
const creatingDialog = ref(false);
const newName = ref('');
const newNameError = ref<string | null>(null);

/** 未保存草稿缓存：切换选中项不丢草稿，切回恢复（替代 confirm，见 §4.4）。 */
const draftCache = new Map<string, string>();

const isBuiltin = computed(() => current.value?.source === 'builtin');
const readOnly = computed(() => isBuiltin.value && !creating.value);

const dirty = computed(() => {
  if (creating.value) return newName.value.trim() !== '' || draftBody.value.trim() !== '';
  if (current.value === null || isBuiltin.value) return false;
  return draftBody.value !== (current.value.body ?? '');
});

function firstLine(body: string, max = 40): string {
  const line = body
    .split('\n')
    .map((l) => l.replace(/^#+\s*/, '').trim())
    .find((l) => l.length > 0);
  if (!line) return '';
  return line.length <= max ? line : line.slice(0, max - 1) + '…';
}

async function loadList(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const r = (await transport.request('asset.skills.list', null)) as {
      items?: SkillSummary[];
      warnings?: string[];
    };
    items.value = Array.isArray(r.items) ? r.items : [];
    warnings.value = Array.isArray(r.warnings) ? r.warnings : [];
    if (selectedName.value !== null && !items.value.some((s) => s.name === selectedName.value)) {
      selectedName.value = null;
      current.value = null;
    }
    if (selectedName.value === null && items.value.length > 0) {
      const first = items.value[0];
      if (first) await select(first.name);
    }
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

async function select(name: string): Promise<void> {
  // 暂存当前草稿（不拦截切换）
  if (selectedName.value !== null && dirty.value && current.value !== null && !isBuiltin.value) {
    draftCache.set(selectedName.value, draftBody.value);
  }
  selectedName.value = name;
  creating.value = false;
  try {
    const r = (await transport.request('asset.skills.get', { name })) as {
      name: string;
      body: string;
      entry?: string;
      source?: string;
    };
    current.value = r;
    draftBody.value = draftCache.get(name) ?? r.body ?? '';
  } catch (e) {
    toast.error(`读取技能失败：${(e as Error).message}`);
    current.value = null;
  }
}

async function save(): Promise<void> {
  if (readOnly.value || saving.value) return;
  if (creating.value) {
    const name = newName.value.trim();
    if (!SKILL_NAME_RE.test(name)) {
      newNameError.value = '技能名只能用小写字母开头，且只能包含小写字母、数字、连字符（≤40 字符）';
      return;
    }
    saving.value = true;
    try {
      await transport.request('asset.skills.save', { name, body: draftBody.value });
      toast.success('已创建技能');
      draftCache.delete(name);
      creatingDialog.value = false;
      creating.value = false;
      await loadList();
      await select(name);
    } catch (e) {
      toast.error(`保存失败：${(e as Error).message}`);
    } finally {
      saving.value = false;
    }
    return;
  }
  if (current.value === null) return;
  saving.value = true;
  try {
    await transport.request('asset.skills.save', {
      name: current.value.name,
      body: draftBody.value,
    });
    draftCache.delete(current.value.name);
    current.value = { ...current.value, body: draftBody.value };
    toast.success('已保存');
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}`);
  } finally {
    saving.value = false;
  }
}

/** 内置技能复制为新技能（§4.3）。 */
async function copyAsNew(): Promise<void> {
  if (current.value === null) return;
  const base = `${current.value.name}-copy`;
  let name = base;
  let n = 1;
  while (items.value.some((s) => s.name === name)) {
    name = `${base}-${++n}`;
    if (n > 99) {
      toast.error('无法生成唯一名称');
      return;
    }
  }
  try {
    await transport.request('asset.skills.save', { name, body: current.value.body });
    toast.success(`已复制为 ${name}`);
    await loadList();
    await select(name);
  } catch (e) {
    toast.error(`复制失败：${(e as Error).message}`);
  }
}

async function removeSkill(): Promise<void> {
  if (current.value === null) return;
  const name = current.value.name;
  confirmDelete.value = false;
  try {
    await transport.request('asset.skills.delete', { name });
    toast.success('已删除');
    draftCache.delete(name);
    selectedName.value = null;
    current.value = null;
    await loadList();
  } catch (e) {
    toast.error(`删除失败：${(e as Error).message}`);
  }
}

function startCreate(): void {
  if (selectedName.value !== null && dirty.value && current.value !== null && !isBuiltin.value) {
    draftCache.set(selectedName.value, draftBody.value);
  }
  selectedName.value = null;
  current.value = null;
  creating.value = true;
  newName.value = '';
  newNameError.value = null;
  draftBody.value = '';
  creatingDialog.value = true;
}

function cancelCreate(): void {
  creatingDialog.value = false;
  creating.value = false;
  newName.value = '';
  newNameError.value = null;
}

function onDocKeydown(e: KeyboardEvent): void {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
    e.preventDefault();
    if (dirty.value && !saving.value) void save();
  }
}

onMounted(() => {
  void loadList();
  document.addEventListener('keydown', onDocKeydown, { capture: true });
});
onBeforeUnmount(() => document.removeEventListener('keydown', onDocKeydown, { capture: true }));

function isDirty(): boolean {
  return dirty.value;
}
defineExpose({ isDirty, reload: loadList });
</script>

<template>
  <AssetLayout
    title="技能"
    :can-create="true"
    :dirty="dirty"
    :saving="saving"
    @create="startCreate"
    @save="save"
    @reload="loadList"
  >
    <template #list>
      <div v-if="loading" class="list-state"><HSpinner size="sm" /><span>加载中…</span></div>
      <div v-else-if="loadError" class="list-state err" role="alert">{{ loadError }}</div>
      <div v-else-if="items.length === 0" class="list-empty">
        <HEmpty
          icon="zap"
          title="还没有自定义技能"
          description="技能是可复用的提示词模板。点右上「新建」创建。"
        />
      </div>
      <AssetListItem
        v-for="s in items"
        v-else
        :key="s.name"
        :active="selectedName === s.name"
        :title="s.name"
        :subtitle="firstLine(s.body ?? '')"
        :badge="s.source === 'builtin' ? '内置' : '自定义'"
        :badge-variant="s.source === 'builtin' ? 'accent' : 'neutral'"
        @select="select(s.name)"
      />
    </template>

    <template #editTitle>
      <span v-if="creating" class="edit-name">新建技能</span>
      <span v-else-if="current" class="edit-name">{{ current.name }}</span>
      <span v-else class="edit-name muted">未选择</span>
    </template>

    <div v-if="warnings.length" class="warn-bar" role="status">
      <div v-for="(w, i) in warnings" :key="i">{{ w }}</div>
    </div>

    <template v-if="creating || current">
      <div v-if="creating" class="create-hint">
        在下方输入技能内容，点右上「保存」或按 Ctrl+S 创建。
      </div>
      <div v-else-if="isBuiltin" class="readonly-hint">
        内置技能不可编辑，可复制为新技能后修改。
      </div>

      <template v-if="current && !creating">
        <label class="field-label" for="skill-entry">触发条件（只读）</label>
        <HInput id="skill-entry" :model-value="current.entry ?? '（无）'" readonly />
      </template>

      <label class="field-label" for="skill-body">SKILL.md 内容</label>
      <HTextarea
        id="skill-body"
        v-model="draftBody"
        class="skill-body font-mono"
        :disabled="readOnly"
        :rows="18"
        spellcheck="false"
        placeholder="# 技能说明&#10;&#10;在这里写提示词模板…"
      />

      <div class="edit-actions">
        <HButton v-if="isBuiltin && !creating" variant="secondary" size="sm" @click="copyAsNew"
          >复制为新技能</HButton
        >
        <HButton v-else-if="!creating" variant="ghost" size="sm" @click="confirmDelete = true"
          >删除技能</HButton
        >
        <span class="spacer" />
        <span class="kbd-hint">Ctrl+S 保存</span>
      </div>
    </template>

    <HEmpty
      v-else
      icon="file-text"
      title="选择一个技能"
      description="从左侧列表选择，或点「新建」创建。"
    />

    <HDialog
      :open="creatingDialog"
      title="新建技能"
      width="sm"
      @update:open="creatingDialog = $event"
    >
      <label class="field-label" for="new-skill">技能名</label>
      <HInput
        id="new-skill"
        v-model="newName"
        placeholder="例如 code-review"
        :error="newNameError !== null"
      />
      <p v-if="newNameError" class="name-err" role="alert">{{ newNameError }}</p>
      <p class="name-hint">只能用小写字母开头，且只能包含小写字母、数字、连字符。</p>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="cancelCreate">取消</HButton>
        <HButton variant="primary" size="sm" :loading="saving" @click="save">创建</HButton>
      </template>
    </HDialog>

    <HDialog
      :open="confirmDelete"
      title="删除技能？"
      width="sm"
      @update:open="confirmDelete = $event"
    >
      <p class="del-p">
        将从磁盘删除 <code>{{ current?.name }}</code> 的 SKILL.md 文件，无法恢复。
      </p>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="confirmDelete = false">取消</HButton>
        <HButton variant="danger" size="sm" @click="removeSkill">删除</HButton>
      </template>
    </HDialog>
  </AssetLayout>
</template>

<style scoped>
.list-state {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-3);
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.list-state.err {
  color: hsl(var(--danger));
}
.list-empty {
  padding: var(--space-2);
}
.edit-name {
  font-size: var(--text-sm);
  font-weight: 600;
}
.edit-name.muted {
  color: hsl(var(--fg-subtle));
  font-weight: 400;
}
.warn-bar {
  padding: var(--space-2);
  border-radius: var(--radius-sm);
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
  font-size: var(--text-xs);
  line-height: 1.5;
}
.readonly-hint {
  padding: var(--space-2);
  border-radius: var(--radius-sm);
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
  font-size: var(--text-xs);
}
.field-label {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.skill-body {
  flex: 1;
  min-height: 160px;
  font-size: var(--text-sm);
}
.edit-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
.spacer {
  flex: 1;
}
.kbd-hint {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}
.create-hint {
  font-size: var(--text-sm);
  color: hsl(var(--fg-muted));
  padding: var(--space-1) 0;
}
.name-hint {
  margin: 2px 0 0;
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
.name-err {
  margin: 2px 0 0;
  font-size: var(--text-xs);
  color: hsl(var(--danger));
}
.del-p {
  margin: 0;
  font-size: var(--text-sm);
  line-height: 1.6;
}
.del-p code {
  font-family: var(--font-mono);
}
</style>
