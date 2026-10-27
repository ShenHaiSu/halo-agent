<!-- src/Web.Client/src/components/assets/MemoryEditor.vue -->
<!-- 记忆编辑器（05 §5.1）：scope 分组，filename 只读，mtype decision/preference/fact/pattern，校验，Ctrl+S。 -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import AssetLayout from './AssetLayout.vue';
import AssetListItem from './AssetListItem.vue';
import HInput from '@/components/base/HInput.vue';
import HTextarea from '@/components/base/HTextarea.vue';
import HSelect from '@/components/base/HSelect.vue';
import HButton from '@/components/base/HButton.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HDialog from '@/components/base/HDialog.vue';

interface MemorySummary {
  filename: string;
  name: string;
  description?: string;
  mtype?: string;
  scope?: string;
}

const MTYPES = [
  { value: 'decision', label: '决策' },
  { value: 'preference', label: '偏好' },
  { value: 'fact', label: '事实' },
  { value: 'pattern', label: '模式' },
];
const SCOPES = [
  { value: 'global', label: '全局（所有项目）' },
  { value: 'project', label: '仅当前项目' },
];

const toast = useToast();

const items = ref<MemorySummary[]>([]);
const loading = ref(true);
const loadError = ref<string | null>(null);

const selectedKey = ref<string | null>(null);
const current = ref<MemorySummary | null>(null);
const currentBody = ref('');

const fName = ref('');
const fDesc = ref('');
const fType = ref('fact');
const fScope = ref('global');
const fBody = ref('');

const saving = ref(false);
const creating = ref(false);
const confirmDelete = ref(false);

const dirty = computed(() => {
  if (creating.value) return fName.value.trim() !== '' || fBody.value.trim() !== '';
  if (current.value === null) return false;
  return (
    fName.value !== (current.value.name ?? '') ||
    fDesc.value !== (current.value.description ?? '') ||
    fType.value !== (current.value.mtype ?? 'fact') ||
    fScope.value !== (current.value.scope ?? 'global') ||
    fBody.value !== currentBody.value
  );
});

const grouped = computed(() => {
  const g: Record<string, MemorySummary[]> = {};
  for (const m of items.value) {
    const k = m.scope ?? 'global';
    (g[k] ??= []).push(m);
  }
  return g;
});

function keyOf(m: MemorySummary): string {
  return `${m.scope ?? 'global'}/${m.filename}`;
}

function scopeLabel(scope: string): string {
  return scope === 'project' ? '当前项目' : '全局';
}

async function loadList(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const r = (await transport.request('asset.memory.list', null)) as { items?: MemorySummary[] };
    items.value = Array.isArray(r.items) ? r.items : [];
    if (selectedKey.value !== null && !items.value.some((m) => keyOf(m) === selectedKey.value)) {
      selectedKey.value = null;
      current.value = null;
    }
    if (selectedKey.value === null && items.value.length > 0) {
      const first = items.value[0];
      if (first) await select(first);
    }
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

async function select(m: MemorySummary): Promise<void> {
  selectedKey.value = keyOf(m);
  creating.value = false;
  try {
    const r = (await transport.request('asset.memory.get', {
      filename: m.filename,
      scope: m.scope ?? 'global',
    })) as { name?: string; description?: string; mtype?: string; scope?: string; body?: string };
    current.value = { ...m, ...r };
    fName.value = r.name ?? '';
    fDesc.value = r.description ?? '';
    fType.value = r.mtype ?? 'fact';
    fScope.value = r.scope ?? 'global';
    fBody.value = r.body ?? '';
    currentBody.value = r.body ?? '';
  } catch (e) {
    toast.error(`读取记忆失败：${(e as Error).message}`);
    current.value = null;
  }
}

function startCreate(): void {
  selectedKey.value = null;
  current.value = null;
  creating.value = true;
  fName.value = '';
  fDesc.value = '';
  fType.value = 'fact';
  fScope.value = 'global';
  fBody.value = '';
  currentBody.value = '';
}

async function save(): Promise<void> {
  if (!fName.value.trim()) {
    toast.warning('名称不能为空');
    return;
  }
  if (fName.value.length > 80) {
    toast.warning('名称不能超过 80 个字符');
    return;
  }
  if (!fDesc.value.trim()) {
    toast.warning('描述不能为空（模型靠它判断何时使用这条记忆）');
    return;
  }
  if (fDesc.value.length > 200) {
    toast.warning('描述不能超过 200 个字符');
    return;
  }
  saving.value = true;
  try {
    const r = (await transport.request('asset.memory.save', {
      filename: creating.value ? undefined : current.value?.filename,
      name: fName.value.trim(),
      description: fDesc.value.trim(),
      mtype: fType.value,
      scope: fScope.value,
      body: fBody.value,
    })) as { ok?: boolean; filename?: string };
    if (r.ok === false) {
      toast.error('保存未成功（输入已保留）');
      return;
    }
    toast.success(creating.value ? '已创建记忆' : '已保存');
    creating.value = false;
    currentBody.value = fBody.value;
    await loadList();
    if (r.filename) {
      const target = items.value.find((m) => m.filename === r.filename);
      if (target) await select(target);
    }
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}`);
  } finally {
    saving.value = false;
  }
}

async function removeMemory(): Promise<void> {
  if (current.value === null) return;
  confirmDelete.value = false;
  try {
    await transport.request('asset.memory.delete', {
      filename: current.value.filename,
      scope: current.value.scope ?? 'global',
    });
    toast.success('已删除');
    selectedKey.value = null;
    current.value = null;
    await loadList();
  } catch (e) {
    toast.error(`删除失败：${(e as Error).message}`);
  }
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
    title="记忆"
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
          icon="layers"
          title="还没有记忆"
          description="模型会在需要时自动创建记忆，你也可以手动添加。"
        />
      </div>
      <template v-else>
        <template v-for="(list, scope) in grouped" :key="scope">
          <div class="group-head">{{ scopeLabel(String(scope)) }}</div>
          <AssetListItem
            v-for="m in list"
            :key="keyOf(m)"
            :active="selectedKey === keyOf(m)"
            :title="m.name"
            :subtitle="m.description"
            :badge="m.mtype"
            badge-variant="neutral"
            @select="select(m)"
          />
        </template>
      </template>
    </template>

    <template #editTitle>
      <span v-if="creating" class="edit-name">新建记忆</span>
      <span v-else-if="current" class="edit-name">{{ current.name }}</span>
      <span v-else class="edit-name muted">未选择</span>
    </template>

    <template v-if="creating || current">
      <div class="form-grid">
        <label class="fl" for="mem-name">名称<span class="req" aria-hidden="true">*</span></label>
        <HInput id="mem-name" v-model="fName" placeholder="简短标题，例如「用户偏好 pnpm」" />

        <label class="fl" for="mem-type">类型<span class="req" aria-hidden="true">*</span></label>
        <HSelect
          id="mem-type"
          :model-value="fType"
          @update:model-value="
            (v: string) => {
              fType = v;
            }
          "
        >
          <option v-for="t in MTYPES" :key="t.value" :value="t.value">{{ t.label }}</option>
        </HSelect>

        <label class="fl" for="mem-scope"
          >作用范围<span class="req" aria-hidden="true">*</span></label
        >
        <HSelect
          id="mem-scope"
          :model-value="fScope"
          :disabled="!creating"
          @update:model-value="
            (v: string) => {
              fScope = v;
            }
          "
        >
          <option v-for="s in SCOPES" :key="s.value" :value="s.value">{{ s.label }}</option>
        </HSelect>

        <label class="fl" for="mem-file">文件名</label>
        <div class="file-ro">
          <code>{{ current?.filename ?? '（保存后由引擎生成）' }}</code>
          <span class="ro-hint">文件名不可修改</span>
        </div>
      </div>

      <label class="fl" for="mem-desc">描述<span class="req" aria-hidden="true">*</span></label>
      <HInput
        id="mem-desc"
        v-model="fDesc"
        placeholder="一句话说明这条记忆的内容（≤200 字符）"
        :maxlength="200"
      />

      <label class="fl" for="mem-body">内容</label>
      <HTextarea
        id="mem-body"
        v-model="fBody"
        class="mem-body font-mono"
        :rows="14"
        spellcheck="false"
        placeholder="记忆的正文…"
      />

      <div class="edit-actions">
        <HButton v-if="!creating && current" variant="ghost" size="sm" @click="confirmDelete = true"
          >删除记忆</HButton
        >
        <span class="spacer" />
        <span class="kbd-hint">Ctrl+S 保存</span>
      </div>
    </template>

    <HEmpty
      v-else
      icon="layers"
      title="选择一条记忆"
      description="从左侧选择，或点「新建」添加。"
    />

    <HDialog
      :open="confirmDelete"
      title="删除记忆？"
      width="sm"
      @update:open="confirmDelete = $event"
    >
      <p class="del-p">
        将删除记忆文件 <code>{{ current?.filename }}</code
        >，无法恢复。
      </p>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="confirmDelete = false">取消</HButton>
        <HButton variant="danger" size="sm" @click="removeMemory">删除</HButton>
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
.group-head {
  padding: var(--space-1) var(--space-2);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-weight: 500;
}
.edit-name {
  font-size: var(--text-sm);
  font-weight: 600;
}
.edit-name.muted {
  color: hsl(var(--fg-subtle));
  font-weight: 400;
}
.form-grid {
  display: grid;
  grid-template-columns: 88px 1fr;
  gap: var(--space-2);
  align-items: center;
}
.fl {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.req {
  color: hsl(var(--danger));
  margin-left: 2px;
}
.file-ro {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}
.file-ro code {
  font-family: var(--font-mono);
  font-size: var(--text-xs);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ro-hint {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  flex: none;
}
.mem-body {
  flex: 1;
  min-height: 120px;
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
.del-p {
  margin: 0;
  font-size: var(--text-sm);
  line-height: 1.6;
}
.del-p code {
  font-family: var(--font-mono);
}
</style>
