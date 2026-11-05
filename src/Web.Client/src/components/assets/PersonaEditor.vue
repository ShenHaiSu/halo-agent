<!-- src/Web.Client/src/components/assets/PersonaEditor.vue -->
<!-- 人格编辑器（05 §5.2）：使用中徽章，应用/清除经 state.setPersona。Ctrl+S 由资源页统一分发。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useSessionStore } from '@/stores/session';
import { useStreamStore } from '@/stores/stream';
import { useToast } from '@/composables/useToast';
import AssetLayout from './AssetLayout.vue';
import AssetListItem from './AssetListItem.vue';
import HInput from '@/components/base/HInput.vue';
import HTextarea from '@/components/base/HTextarea.vue';
import HButton from '@/components/base/HButton.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HDialog from '@/components/base/HDialog.vue';

const toast = useToast();
const session = useSessionStore();
// P23-R5：跑轮中切换人格被后端拒绝，前端提前给反馈并禁用按钮。
const busy = computed(() => useStreamStore().busy);
function isBusyError(e: unknown): boolean {
  const code = (e as { code?: unknown })?.code;
  if (code === 'state.busy') return true;
  const msg = String((e as Error)?.message ?? '');
  return msg.includes('state.busy') || msg.includes('有回合正在执行');
}

const names = ref<string[]>([]);
const loading = ref(true);
const loadError = ref<string | null>(null);

const selected = ref<string | null>(null);
const fName = ref('');
const fBody = ref('');
const originBody = ref('');
const saving = ref(false);
const creating = ref(false);
const query = ref('');
const confirmDelete = ref(false);
/** 待删除的目标名：打开对话框时快照，防止对话框期间切走选中项导致删错文件。 */
const pendingDelete = ref<string | null>(null);
const deleting = ref(false);

const dirty = computed(() => {
  if (creating.value) return fName.value.trim() !== '' || fBody.value.trim() !== '';
  return fBody.value !== originBody.value;
});

const count = computed(() => names.value.length);

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase();
  if (!q) return names.value;
  return names.value.filter((n) => n.toLowerCase().includes(q));
});

async function loadList(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const r = (await transport.request('asset.personas.list', null)) as { items?: string[] };
    names.value = Array.isArray(r.items) ? r.items : [];
    if (selected.value !== null && !names.value.includes(selected.value)) {
      selected.value = null;
      fBody.value = '';
      originBody.value = '';
    }
    if (selected.value === null && names.value.length > 0) {
      const first = names.value[0];
      if (first) await select(first);
    }
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

async function select(name: string): Promise<void> {
  selected.value = name;
  creating.value = false;
  fName.value = name;
  try {
    const r = (await transport.request('asset.personas.get', { name })) as {
      name?: string;
      body?: string;
    };
    fBody.value = r.body ?? '';
    originBody.value = r.body ?? '';
  } catch (e) {
    toast.error(`读取人格失败：${(e as Error).message}`);
    fBody.value = '';
    originBody.value = '';
  }
}

function startCreate(): void {
  creating.value = true;
  selected.value = null;
  fName.value = '';
  fBody.value = '';
  originBody.value = '';
}

async function save(): Promise<void> {
  const name = fName.value.trim();
  if (!name) {
    toast.warning('名称不能为空');
    return;
  }
  if (name.length > 40) {
    toast.warning('名称不能超过 40 个字符');
    return;
  }
  if (/[\\/:*?"<>|]/.test(name)) {
    toast.warning('名称不能包含 \\ / : * ? " < > | 等字符');
    return;
  }
  saving.value = true;
  try {
    await transport.request('asset.personas.save', { name, body: fBody.value });
    toast.success(creating.value ? '已创建人格' : '已保存');
    const wasCreating = creating.value;
    creating.value = false;
    originBody.value = fBody.value;
    await loadList();
    if (wasCreating) await select(name);
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}`);
  } finally {
    saving.value = false;
  }
}

/** 应用为人格（当前会话生效，经 state.setPersona）。 */
async function apply(): Promise<void> {
  if (busy.value) { toast.warning('回合执行中不可切换人格'); return; }
  const name = (selected.value ?? fName.value).trim();
  if (!name) return;
  try {
    await session.setPersona(name);
    toast.success(`已切换人格：${name}`);
  } catch (e) {
    if (isBusyError(e)) toast.warning('有回合正在执行，暂不能切换人格');
    else toast.error('切换失败');
  }
}

async function clearPersona(): Promise<void> {
  if (busy.value) { toast.warning('回合执行中不可切换人格'); return; }
  try {
    await session.setPersona('');
    toast.success('已清除人格');
  } catch (e) {
    if (isBusyError(e)) toast.warning('有回合正在执行，暂不能切换人格');
    else toast.error('清除失败');
  }
}


function requestDelete(): void {
  if (!selected.value || deleting.value) return;
  if (dirty.value) {
    toast.warning('请先保存或撤销当前改动，再删除');
    return;
  }
  pendingDelete.value = selected.value; // 快照：之后 selected 变化也不影响删除目标
  confirmDelete.value = true;
}

/** 删除人格文件；若删除的是当前会话使用中的人格，先解绑再删。 */
async function removePersona(): Promise<void> {
  const name = pendingDelete.value; // 用快照名，不用 selected（见 requestDelete 注释）
  confirmDelete.value = false;
  if (!name || deleting.value) {
    pendingDelete.value = null;
    return;
  }
  deleting.value = true;
  try {
    // ① 先解绑：避免"文件已删但会话仍指向该名"的中间态，也避免同名内置人格被回落命中。
    const wasInUse = session.persona === name;
    if (wasInUse) {
      try {
        await session.setPersona('');
      } catch (e) {
        // P23-R5 §A3：跑轮中解绑会被后端 busy 守卫拦下，给专门文案而非“无法解除绑定”。
        if (isBusyError(e)) toast.warning('回合执行中，暂不能删除使用中的人格');
        else toast.warning('无法解除当前会话的人格绑定，已取消删除');
        return; // 解绑失败就不删，避免“人格还在跑但文件没了”
      }
    }

    // ② 再删文件
    try {
      await transport.request('asset.personas.delete', { name });
      toast.success('已删除');
      if (wasInUse) toast.info('该人格正在使用，已自动清除当前会话的人格设置');
    } catch (e) {
      toast.error(`删除失败：${(e as Error).message}`);
      if (wasInUse) toast.warning('会话人格已清除，但文件删除失败，可稍后重试');
      return;
    }

    // ③ 清空编辑区并重载列表
    selected.value = null;
    fName.value = '';
    fBody.value = '';
    originBody.value = '';
    await loadList();
  } finally {
    pendingDelete.value = null;
    deleting.value = false;
  }
}

onMounted(() => {
  void loadList();
});

function isDirty(): boolean {
  return dirty.value;
}
/** Ctrl+S 由资源页统一分发调用。 */
function saveIfDirty(): void {
  if (dirty.value && !saving.value) void save();
}
defineExpose({ isDirty, reload: loadList, saveIfDirty, count });
</script>

<template>
  <AssetLayout
    title="人格"
    list-label="人格列表"
    :query="query"
    query-placeholder="搜索人格…"
    :can-create="true"
    :dirty="dirty"
    :saving="saving"
    @create="startCreate"
    @save="save"
    @reload="loadList"
    @update:query="query = $event"
  >
    <template #list>
      <div v-if="loading" class="list-state" role="status"><HSpinner size="sm" /><span>加载中…</span></div>
      <div v-else-if="loadError" class="list-state err" role="alert">{{ loadError }}</div>
      <div v-else-if="names.length === 0" class="list-empty">
        <HEmpty
          icon="user"
          title="还没有自定义人格"
          description="人格会改变模型的语气与行为风格。点右上「新建」创建。"
        />
      </div>
      <div v-else-if="filtered.length === 0" class="list-empty">
        <HEmpty icon="user" title="没有匹配的人格" description="换个关键词试试，或清空搜索。" />
      </div>
      <AssetListItem
        v-for="n in filtered"
        v-else
        :key="n"
        :active="selected === n"
        :title="n"
        :badge="session.persona === n ? '使用中' : undefined"
        badge-variant="accent"
        @select="select(n)"
      />
    </template>

    <template #editTitle>
      <span v-if="creating" class="edit-name">新建人格</span>
      <span v-else-if="selected" class="edit-name">{{ selected }}</span>
      <span v-else class="edit-name muted">未选择</span>
    </template>

    <template v-if="creating || selected">
      <div class="name-row">
        <HInput v-model="fName" placeholder="人格名称" :disabled="!creating" />
        <HButton size="sm" variant="secondary" :disabled="!selected || creating || busy" @click="apply">
          应用到当前会话
        </HButton>
        <HButton v-if="session.persona" size="sm" variant="ghost" :disabled="busy" @click="clearPersona"
          >清除人格</HButton
        >
        <HButton
          v-if="selected && !creating"
          size="sm"
          variant="ghost"
          :disabled="dirty"
          :loading="deleting"
          @click="requestDelete"
          >删除人格</HButton
        >
      </div>

      <div v-if="session.persona" class="current-hint">
        <span
          >当前会话使用的人格：<strong>{{ session.persona }}</strong></span
        >
      </div>

      <HTextarea
        v-model="fBody"
        class="persona-body font-mono"
        :rows="18"
        spellcheck="false"
        placeholder="用自然语言描述你希望模型的语气、关注点与行为边界…"
      />
    </template>

    <HEmpty v-else icon="user" title="选择一个人格" description="从左侧选择，或点「新建」创建。" />

    <HDialog
      :open="confirmDelete"
      title="删除人格？"
      width="sm"
      @update:open="confirmDelete = $event"
    >
      <p class="del-p">
        将从磁盘删除 <code>{{ pendingDelete }}.md</code>，无法恢复。
        <template v-if="session.persona === pendingDelete">
          该人格正在当前会话使用，删除后会先解除绑定再删除。
        </template>
      </p>
      <p class="del-p del-p-hint">若该名称与内置人格同名，删除后将回退为内置版本。</p>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="confirmDelete = false">取消</HButton>
        <HButton variant="danger" size="sm" :loading="deleting" @click="removePersona"
          >删除</HButton
        >
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
.name-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  flex-wrap: wrap;
}
.current-hint {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.persona-body {
  flex: 1;
  min-height: 200px;
  font-size: var(--text-sm);
}
.del-p {
  margin: 0;
  font-size: var(--text-sm);
  line-height: 1.6;
}
.del-p-hint {
  color: hsl(var(--fg-muted));
  font-size: var(--text-xs);
}
</style>
