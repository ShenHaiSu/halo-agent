<!-- src/Web.Client/src/components/settings/pages/ModelPage.vue -->
<!-- P27 定稿：左 220px Provider 一级列表（不展示 Model）+ 右单卷轴双区块（连接 / 模型密表）。
     依据 docs/P27-FrontEnd-Enhance/定稿-Provider-Model双区块版.md。
     无 tab / 锚点条 / tag / 描述段 / 生效行；说明只以 ⓘ 存在。 -->
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HDialog from '@/components/base/HDialog.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { HeaderRow } from '../fields/HeadersEditor.vue';
import ProviderTree from '../provider/ProviderTree.vue';
import ProviderConnForm from '../provider/ProviderConnForm.vue';
import ModelDenseTable from '../provider/ModelDenseTable.vue';
import {
  mainName,
  normalizeProvidersTree,
  validateProvidersTree,
  type ProviderEdit,
  type ProvidersTree,
} from '../providerEditing';
import {
  DENSITY_STORAGE_KEY,
  canSetActive,
  normalizeDensity,
  type Density,
} from '../providerFormat';

const toast = useToast();
const loading = ref(true);
const saving = ref(false);
const loadError = ref<string | null>(null);
const migrationRequired = ref(false);
const globalMode = ref('system');

const tree = ref<ProvidersTree>({ activeProvider: '', activeModel: '', providers: [] });
const originalJson = ref('');
/** 右侧当前展示的 Provider id（UI 态，不入 dirty 快照） */
const currentId = ref('');
const fieldErrors = ref<Record<string, string>>({});
const pendingDeleteProvider = ref<string | null>(null);
const pendingDeleteModel = ref<string | null>(null);
/** 拉取状态文案（UI 态；ModelProbe 弹窗确认并入后刷新） */
const fetchStatus = ref('未拉取');
/** 两步确认覆盖旧结构/损坏配置 */
const confirmOverwrite = ref(false);
const overwriteRequired = ref(false);
const overwriteMsg = ref('');

/** 密度偏好：紧凑 28px / 舒适 32px（localStorage: density，见定稿 §7） */
const density = ref<Density>('dense');

const dirty = computed(() => JSON.stringify(snapshotForDirty()) !== originalJson.value);

const current = computed<ProviderEdit | undefined>(() =>
  tree.value.providers.find((p) => p.id === currentId.value)
);

/** 当前 Provider 的字段错误（去掉 `${id}/` 前缀） */
const currentErrors = computed<Record<string, string>>(() => {
  const pid = currentId.value;
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(fieldErrors.value)) {
    const prefix = `${pid}/-.`;
    if (k.startsWith(prefix)) out[k.slice(prefix.length)] = v;
    else if (k.startsWith(`${pid}/`)) {
      // 模型级错误留在密表内展示
    }
  }
  return out;
});

/** 当前 Provider 的模型级错误：`${modelId}.${field}` */
const modelErrors = computed<Record<string, string>>(() => {
  const pid = currentId.value;
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(fieldErrors.value)) {
    const prefix = `${pid}/`;
    if (!k.startsWith(prefix)) continue;
    const rest = k.slice(prefix.length); // `${modelId}.${field}`
    if (rest.startsWith('-.')) continue;
    const dot = rest.lastIndexOf('.');
    if (dot <= 0) continue;
    out[`${rest.slice(0, dot)}.${rest.slice(dot + 1)}`] = v;
  }
  return out;
});

/** 快照排除 expanded(UI 态)，与原有 dirty 语义完全一致。 */
function snapshotForDirty(): unknown {
  return {
    providers: tree.value.providers.map((p) => ({
      id: p.id,
      displayName: p.displayName,
      baseUrl: p.baseUrl,
      newKey: p.newKey,
      clearKey: p.clearKey,
      headersEnabled: p.headersEnabled,
      headers: p.headers,
      network: p.network,
      responseStore: p.responseStore,
      models: p.models.map((m) => ({
        id: m.id,
        displayName: m.displayName,
        protocol: m.protocol,
        contextWindow: m.contextWindow,
        maxTokens: m.maxTokens,
        acceptsImages: m.acceptsImages,
        includeReasoningHistory: m.includeReasoningHistory,
      })),
    })),
  };
}

function markDirty(): void {
  fieldErrors.value = {};
}

function applyDensity(): void {
  try {
    window.localStorage.setItem(DENSITY_STORAGE_KEY, density.value);
  } catch {
    /* 隐私模式忽略 */
  }
}

function setDensity(d: Density): void {
  density.value = d;
  applyDensity();
}

/** 探测勾选确认：去重合并进 model 列表（精确 Ordinal 匹配），走正常保存链。 */
function onProbeConfirm(names: string[]): void {
  const p = current.value;
  if (!p) return;
  const have = new Set(p.models.map((m) => m.id));
  let added = 0;
  for (const name of names) {
    const id = (name ?? '').trim();
    if (!id || have.has(id)) continue;
    p.models.push({
      id,
      displayName: '',
      protocol: 'chat',
      contextWindow: null,
      maxTokens: null,
      acceptsImages: false,
      includeReasoningHistory: true,
      expanded: false,
    });
    have.add(id);
    added += 1;
  }
  fetchStatus.value = `已拉取到 ${names.length} 个`;
  markDirty();
  toast.success(added > 0 ? `已加入 ${added} 个（未保存）` : '所选均已在列表');
}

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const s = (await transport.request('settings.get', null)) as {
      activeProvider?: string;
      activeModel?: string;
      migrationRequired?: boolean;
      providers?: unknown;
      proxy?: { mode?: string };
    };
    const t = normalizeProvidersTree(s);
    t.providers.forEach((p) => {
      p.expanded = false;
      p.models.forEach((m) => {
        m.expanded = false;
      });
    });
    tree.value = t;
    currentId.value = t.providers[0]?.id ?? '';
    migrationRequired.value = s.migrationRequired === true;
    const gm = String(s.proxy?.mode ?? 'system').toLowerCase();
    globalMode.value =
      gm === 'manual' ? 'manual' : gm === 'direct' || gm === 'auto' ? 'direct' : 'system';
    confirmOverwrite.value = false;
    overwriteRequired.value = false;
    overwriteMsg.value = '';
    fetchStatus.value = '未拉取';
    originalJson.value = JSON.stringify(snapshotForDirty());
    fieldErrors.value = {};
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

function selectProvider(id: string): void {
  if (id === currentId.value) return;
  if (dirty.value && !window.confirm('有未保存改动，切换将丢失，继续？')) return;
  currentId.value = id;
  fieldErrors.value = {};
  fetchStatus.value = '未拉取';
}

function createProvider(id: string): void {
  if (dirty.value && !window.confirm('有未保存改动，继续将丢失？')) return;
  tree.value.providers.push({
    id,
    displayName: '',
    baseUrl: '',
    apiKeySet: false,
    newKey: '',
    clearKey: false,
    headersEnabled: true,
    headers: [],
    network: 'follow',
    responseStore: 'default',
    models: [],
    expanded: false,
  });
  currentId.value = id;
  markDirty();
}

function addModel(): void {
  const p = current.value;
  if (!p) return;
  p.models.push({
    id: '',
    displayName: '',
    protocol: 'chat',
    contextWindow: null,
    maxTokens: null,
    acceptsImages: false,
    includeReasoningHistory: true,
    expanded: true,
  });
  markDirty();
}

function requestDeleteModel(mid: string): void {
  pendingDeleteModel.value = mid;
}

function confirmDeleteModel(): void {
  const mid = pendingDeleteModel.value;
  pendingDeleteModel.value = null;
  const p = current.value;
  if (!p || mid === null) return;
  p.models = p.models.filter((m) => m.id !== mid);
  // 删命中 active 的模型：同时清 active（后端亦不容 active 指向不存在模型）
  if (tree.value.activeProvider === p.id && tree.value.activeModel === mid) {
    tree.value.activeProvider = '';
    tree.value.activeModel = '';
  }
  markDirty();
  toast.success('已删除模型（未保存）');
}

function requestDeleteProvider(): void {
  if (!current.value) return;
  pendingDeleteProvider.value = current.value.id;
}

function confirmDeleteProvider(): void {
  const pid = pendingDeleteProvider.value;
  pendingDeleteProvider.value = null;
  if (!pid) return;
  const hitActive = tree.value.activeProvider === pid;
  tree.value.providers = tree.value.providers.filter((x) => x.id !== pid);
  if (hitActive) {
    tree.value.activeProvider = '';
    tree.value.activeModel = '';
  }
  if (currentId.value === pid) currentId.value = tree.value.providers[0]?.id ?? '';
  markDirty();
  toast.success('已删除 Provider（未保存）');
}

/** 设为使用中：空 Provider 禁止；dirty 时确认后仍可切。 */
function setActive(): void {
  const p = current.value;
  if (!p) return;
  if (!canSetActive(p)) {
    toast.warning('空 Provider 不可设为使用中，请先添加模型');
    return;
  }
  if (dirty.value && !window.confirm('有未保存改动，仍切换使用中的模型？')) return;
  // 沿用现有模型（默认第一个），不擅自新建
  const mid = p.models.some((m) => m.id === tree.value.activeModel)
    ? tree.value.activeModel
    : p.models[0]!.id;
  tree.value.activeProvider = p.id;
  tree.value.activeModel = mid;
  markDirty();
  toast.success(`已设为使用中：${p.id} / ${mainName(mid, p.models.find((m) => m.id === mid)?.displayName)}`);
}

function toSavePayloadWithForce(force: boolean): Record<string, unknown> {
  const providers: Record<string, unknown> = {};
  for (const p of tree.value.providers) {
    providers[p.id] = {
      // 缺席/显式空区分：整树下发一律显式传 displayName，空字串=清除（后端 NormalizeAlias→null）。
      displayName: p.displayName,
      baseUrl: p.baseUrl,
      apiKey: p.newKey || undefined,
      clearKey: p.clearKey || undefined,
      headersEnabled: p.headersEnabled,
      headers: p.headers.filter((h) => h.key.trim() !== ''),
      network: p.network,
      responseStore: p.responseStore,
      models: p.models.map((m) => ({
        id: m.id,
        displayName: m.displayName,
        protocol: m.protocol,
        contextWindow: m.contextWindow,
        maxTokens: m.maxTokens,
        acceptsImages: m.acceptsImages,
        includeReasoningHistory: m.includeReasoningHistory,
      })),
    };
  }
  return force ? { providers, force: true } : { providers };
}

/** 两步确认：migrationRequired 或 config.overwriteRequired 时第一次点击仅进入待确认态。 */
function needConfirm(): boolean {
  return migrationRequired.value || overwriteRequired.value;
}

function onSaveClick(): void {
  if (needConfirm() && !confirmOverwrite.value) {
    confirmOverwrite.value = true;
    toast.warning('旧配置文件已自动备份，确认用当前整树覆盖请再点一次保存');
    return;
  }
  void save(needConfirm() && confirmOverwrite.value);
}

function rpcCode(e: unknown): string {
  const o = (e ?? {}) as Record<string, unknown>;
  if (typeof o['code'] === 'string') return o['code'] as string;
  const d = o['data'] as Record<string, unknown> | undefined;
  if (d && typeof d['code'] === 'string') return d['code'] as string;
  const err = o['error'] as Record<string, unknown> | undefined;
  if (err && typeof err['code'] === 'string') return err['code'] as string;
  return '';
}

async function save(force = false): Promise<void> {
  if (saving.value) return;
  const errs = validateProvidersTree({
    activeProvider: tree.value.activeProvider,
    activeModel: tree.value.activeModel,
    providers: tree.value.providers.map((p) => ({
      id: p.id,
      displayName: p.displayName,
      baseUrl: p.baseUrl,
      apiKeySet: p.apiKeySet,
      newKey: p.newKey,
      clearKey: p.clearKey,
      headersEnabled: p.headersEnabled,
      headers: p.headers,
      network: p.network,
      responseStore: p.responseStore,
      models: p.models,
    })),
  });
  const map: Record<string, string> = {};
  for (const e of errs) map[`${e.provider}/${e.model ?? '-'}.${e.field}`] = e.message;
  fieldErrors.value = map;
  if (errs.length > 0) {
    toast.warning('请先修正表单中的错误');
    return;
  }
  saving.value = true;
  try {
    const r = (await transport.request('settings.saveProviders', toSavePayloadWithForce(force))) as {
      ok?: boolean;
      warnings?: string[];
    };
    if (r.ok === false) {
      toast.error('保存失败（输入已保留）');
      return;
    }
    for (const w of r.warnings ?? []) toast.warning(w);
    // apiKey 已落盘：清空明文
    tree.value.providers.forEach((p) => {
      if (p.newKey) p.apiKeySet = true;
      p.newKey = '';
      p.clearKey = false;
    });
    originalJson.value = JSON.stringify(snapshotForDirty());
    migrationRequired.value = false;
    overwriteRequired.value = false;
    overwriteMsg.value = '';
    confirmOverwrite.value = false;
    toast.success('已保存');
  } catch (e) {
    if (rpcCode(e) === 'config.overwriteRequired') {
      overwriteRequired.value = true;
      overwriteMsg.value = (e as Error).message;
      confirmOverwrite.value = false;
      toast.warning(
        '配置文件为旧结构或已损坏，原文件已自动备份，确认用当前整树覆盖请再点一次保存（输入已保留）'
      );
      return;
    }
    toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
  } finally {
    saving.value = false;
  }
}

function isDirty(): boolean {
  return dirty.value;
}
function resetToSaved(): void {
  void load();
}
defineExpose({ isDirty, resetToSaved });

// 密度偏好：挂到本页容器上（--settings-ih 由 CSS 变量下发给 FieldRow/input）
onMounted(() => {
  try {
    density.value = normalizeDensity(window.localStorage.getItem(DENSITY_STORAGE_KEY));
  } catch {
    density.value = 'dense';
  }
  void load();
});

watch(density, applyDensity);
</script>

<template>
  <div class="mpage" :class="{ roomy: density === 'roomy' }">
    <!-- 顶栏 sticky：标题 | 紧凑/舒适 | dirty | 保存 | 放弃 -->
    <div class="mpage-top">
      <h2 class="mpage-title">模型</h2>
      <div class="seg" role="group" aria-label="密度">
        <button
          type="button"
          class="seg-btn"
          :class="{ on: density === 'dense' }"
          :aria-pressed="density === 'dense'"
          @click="setDensity('dense')"
        >
          紧凑
        </button>
        <button
          type="button"
          class="seg-btn"
          :class="{ on: density === 'roomy' }"
          :aria-pressed="density === 'roomy'"
          @click="setDensity('roomy')"
        >
          舒适
        </button>
      </div>
      <span class="spacer" />
      <span v-if="dirty" class="dirty-tag" role="status">有未保存的改动</span>
      <HButton
        size="sm"
        :variant="needConfirm() && confirmOverwrite ? 'danger' : 'primary'"
        :disabled="!dirty"
        :loading="saving"
        @click="onSaveClick"
        >{{ needConfirm() && confirmOverwrite ? '确认覆盖并保存' : '保存' }}</HButton
      >
      <HButton size="sm" variant="secondary" :disabled="!dirty" @click="resetToSaved">放弃</HButton>
    </div>

    <div v-if="migrationRequired" class="mpage-note" role="alert">
      <HaloIcon name="alert" :size="13" />
      配置结构已升级（旧文件已自动备份），请重建 providers 后保存。
    </div>
    <div v-if="overwriteRequired" class="mpage-note" role="alert">
      <HaloIcon name="alert" :size="13" />
      检测到旧结构/损坏配置（{{ overwriteMsg }}），已备份，确认覆盖请再点一次保存。
    </div>

    <div v-if="loading" class="mpage-state"><HSpinner size="sm" /><span>加载中…</span></div>
    <div v-else-if="loadError" class="mpage-state err" role="alert">
      <span>读取设置失败：{{ loadError }}</span>
      <HButton size="sm" variant="secondary" @click="load">重试</HButton>
    </div>

    <div v-else class="mpage-main">
      <!-- 左：Provider 一级列表 -->
      <aside class="mpage-tree">
        <ProviderTree
          :providers="tree.providers"
          :current-id="currentId"
          :active-provider="tree.activeProvider"
          @select="selectProvider"
          @create="createProvider"
        />
      </aside>

      <!-- 右：单卷轴双区块 -->
      <div class="mpage-detail">
        <template v-if="current">
          <div class="detail-head">
            <h3 class="detail-title">{{ mainName(current.id, current.displayName) }}</h3>
            <span class="detail-id">{{ current.id }}</span>
            <HButton
              size="sm"
              variant="secondary"
              :disabled="current.models.length === 0"
              title="把该 Provider 设为使用中"
              @click="setActive"
            >
              设为使用中
            </HButton>
          </div>

          <ProviderConnForm
            :provider="current"
            :errors="currentErrors"
            :global-mode="globalMode"
            :fetch-status="fetchStatus"
            @change="markDirty"
            @update:clear-key="
              (v: boolean) => {
                if (!current) return;
                current.clearKey = v;
                markDirty();
              }
            "
            @update:headers-enabled="
              (v: boolean) => {
                if (!current) return;
                current.headersEnabled = v;
                markDirty();
              }
            "
            @update:headers="
              (rows: HeaderRow[]) => {
                if (!current) return;
                current.headers = rows;
                markDirty();
              }
            "
            @probe-confirm="onProbeConfirm"
            @request-delete="requestDeleteProvider"
          />

          <ModelDenseTable
            :provider-id="current.id"
            :models="current.models"
            :tree="tree"
            :errors="modelErrors"
            @change="markDirty"
            @add-model="addModel"
            @delete-model="requestDeleteModel"
            @set-active="setActive"
          />
        </template>

        <div v-else class="mpage-empty" role="status">
          <p>还没有 Provider。点左侧「新建 Provider」开始配置，或用「模型列表」拉取可用模型。</p>
        </div>
      </div>
    </div>

    <!-- 底栏 sticky：全局代理 + dirty -->
    <div class="mpage-foot">
      <span class="foot-hint">全局代理：{{ globalMode }}</span>
      <span class="spacer" />
      <span v-if="dirty" class="dirty-tag" role="status">有未保存的改动</span>
    </div>

    <HDialog
      :open="pendingDeleteProvider !== null"
      title="删除 Provider？"
      @close="pendingDeleteProvider = null"
    >
      <p>
        将删除 {{ pendingDeleteProvider }} 及其下全部模型（未保存）。若命中 active，需先选新
        active 或置未配置态。
      </p>
      <template #footer>
        <HButton size="sm" variant="ghost" @click="pendingDeleteProvider = null">取消</HButton>
        <HButton size="sm" variant="danger" @click="confirmDeleteProvider">删除</HButton>
      </template>
    </HDialog>
    <HDialog
      :open="pendingDeleteModel !== null"
      title="删除模型？"
      @close="pendingDeleteModel = null"
    >
      <p>将删除 {{ pendingDeleteModel }}（未保存）。若命中 active，需先选新 active。</p>
      <template #footer>
        <HButton size="sm" variant="ghost" @click="pendingDeleteModel = null">取消</HButton>
        <HButton size="sm" variant="danger" @click="confirmDeleteModel">删除</HButton>
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.mpage {
  /* 唯一高度变量：紧凑 28 / 舒适 32（body.roomy 的等价物，只作用于本页） */
  --settings-ih: 28px;
  display: flex;
  flex-direction: column;
  min-height: 0;
  height: 100%;
  min-width: 0;
}
.mpage.roomy {
  --settings-ih: 32px;
}
/* 行高统一的控件：input / select / textarea 引用 --settings-ih */
.mpage :deep(input:not([type='checkbox']):not([type='radio'])),
.mpage :deep(select),
.mpage :deep(textarea) {
  transition:
    border-color 120ms ease-out,
    box-shadow 120ms ease-out;
}
.mpage :deep(input:not([type='number']):not([type='checkbox']):not([type='radio'])),
.mpage :deep(select) {
  height: var(--settings-ih);
}
.mpage :deep(input:focus-visible),
.mpage :deep(select:focus-visible),
.mpage :deep(textarea:focus-visible) {
  /* focus 环：accent 边框 + 3px 15% 光晕（定稿 §3） */
  box-shadow: 0 0 0 3px hsl(var(--accent) / 0.15);
}

.mpage-top {
  position: sticky;
  top: 0;
  z-index: 3;
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1-5) 0;
  background: hsl(var(--bg));
  border-bottom: 1px solid hsl(var(--border-subtle));
  flex-wrap: wrap;
}
.mpage-title {
  margin: 0;
  font-size: var(--text-lg);
  font-weight: 600;
  color: hsl(var(--fg));
}
.spacer {
  flex: 1;
  min-width: 0;
}
.dirty-tag {
  font-size: var(--text-2xs);
  color: hsl(var(--warning));
  font-weight: 600;
  white-space: nowrap;
}
.mpage-note {
  display: flex;
  align-items: center;
  gap: var(--space-1-5);
  margin-top: var(--space-1-5);
  padding: var(--space-1-5) var(--space-2);
  border-radius: var(--radius-sm);
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
  font-size: var(--text-xs);
}
.mpage-state {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: hsl(var(--fg-muted));
  font-size: var(--text-sm);
  padding: var(--space-4) 0;
}
.mpage-state.err {
  color: hsl(var(--danger));
  flex-direction: column;
  align-items: flex-start;
}
.mpage-main {
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: 220px minmax(0, 1fr);
  gap: var(--space-2);
  padding: var(--space-2) 0;
}
.mpage-tree {
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow-y: auto;
  padding-right: 2px;
}
.mpage-detail {
  min-width: 0;
  min-height: 0;
  overflow-y: auto;
  padding-right: 2px;
}
.detail-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
  padding: var(--space-1) 0 var(--space-1-5);
}
.detail-title {
  margin: 0;
  font-size: var(--text-lg);
  font-weight: 700;
  color: hsl(var(--fg));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.detail-id {
  font-family: var(--font-mono);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}
.detail-head > :last-child {
  margin-left: auto;
  flex: none;
}
.mpage-empty {
  padding: var(--space-6) var(--space-2);
  text-align: center;
  color: hsl(var(--fg-subtle));
  font-size: var(--text-sm);
}
.mpage-foot {
  position: sticky;
  bottom: 0;
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1-5) 0;
  background: hsl(var(--bg));
  border-top: 1px solid hsl(var(--border-subtle));
  min-width: 0;
}
.foot-hint {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-muted));
}
/* 三段 seg：底 bg-muted，选中白底 accent 字 */
.seg {
  display: inline-flex;
  gap: 2px;
  padding: 2px;
  background: hsl(var(--bg-muted));
  border-radius: var(--radius-md);
  flex: none;
}
.seg-btn {
  height: 24px;
  padding: 0 var(--space-2-5);
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  color: hsl(var(--fg-muted));
  font-size: var(--text-xs);
  line-height: 1;
  cursor: pointer;
  white-space: nowrap;
  transition:
    background-color 130ms ease-out,
    color 130ms ease-out;
}
.seg-btn:hover {
  color: hsl(var(--fg));
}
.seg-btn.on {
  background: hsl(var(--bg-elevated));
  color: hsl(var(--accent));
  font-weight: 600;
  box-shadow: var(--shadow-sm);
}
.seg-btn:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: 1px;
}
/* 响应式：<920px 左右变上下 */
@media (max-width: 920px) {
  .mpage-main {
    grid-template-columns: minmax(0, 1fr);
  }
  .mpage-tree {
    max-height: 30vh;
    border-bottom: 1px solid hsl(var(--border-subtle));
    padding-bottom: var(--space-1-5);
  }
}
@media (prefers-reduced-motion: reduce) {
  .seg-btn,
  .mpage :deep(input),
  .mpage :deep(select),
  .mpage :deep(textarea) {
    transition: none;
  }
}
</style>
