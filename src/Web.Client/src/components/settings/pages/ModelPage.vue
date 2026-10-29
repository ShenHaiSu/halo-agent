<!-- src/Web.Client/src/components/settings/pages/ModelPage.vue -->
<!-- P10：设置/模型单页内两栏：左 Provider 列表 + 右详情 + 可伸缩模型卡片。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import SecretField from '../fields/SecretField.vue';
import HeadersEditor, { type HeaderRow } from '../fields/HeadersEditor.vue';
import ModelProbe from '../fields/ModelProbe.vue';
import HInput from '@/components/base/HInput.vue';
import HSelect from '@/components/base/HSelect.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HSwitch from '@/components/base/HSwitch.vue';
import HDialog from '@/components/base/HDialog.vue';
import { headerPlaceholderTooltip } from '../fields/headerPlaceholders';
import { normalizeProvidersTree, validateProvidersTree, type ProvidersTree } from '../providerEditing';

/** 上下文/输出上限预设（input + ButtonGroup，不用滑杆）。1M = 1000000，2M = 2000000。 */
const CTX_PRESETS = [
  { label: '128K', value: 128000 },
  { label: '256K', value: 256000 },
  { label: '512K', value: 512000 },
  { label: '1M', value: 1000000 },
  { label: '2M', value: 2000000 },
] as const;
const MAXTOK_PRESETS = [
  { label: '5k', value: 5000 },
  { label: '20k', value: 20000 },
  { label: '50k', value: 50000 },
  { label: '60k', value: 60000 },
  { label: '128k', value: 128000 },
] as const;

const toast = useToast();
const loading = ref(true);
const saving = ref(false);
const activating = ref(false);
const busy = ref(false);
const loadError = ref<string | null>(null);
const migrationRequired = ref(false);
const globalMode = ref('auto');

const tree = ref<ProvidersTree>({ activeProvider: '', activeModel: '', providers: [] });
const originalJson = ref('');
const selectedId = ref('');
const newProviderMode = ref(false);
const newProviderId = ref('');
const fieldErrors = ref<Record<string, string>>({});
const showDeleteProvider = ref(false);
/** 方案A：migrationRequired 下保存要点两次确认覆盖（第二次才带 force=true）。 */
const confirmOverwrite = ref(false);
const pendingDeleteModel = ref<{ pid: string; mid: string } | null>(null);

const selected = computed(() => tree.value.providers.find((p) => p.id === selectedId.value) ?? null);
const dirty = computed(() => JSON.stringify(snapshotForDirty()) !== originalJson.value);

function snapshotForDirty(): unknown {
  return {
    providers: tree.value.providers.map((p) => ({
      id: p.id, displayName: p.displayName, baseUrl: p.baseUrl,
      newKey: p.newKey, clearKey: p.clearKey, headersEnabled: p.headersEnabled,
      headers: p.headers, useProxy: p.useProxy,
      models: p.models.map((m) => ({ ...m })),
    })),
  };
}

function markDirty(): void { fieldErrors.value = {}; }

const headersTooltip = headerPlaceholderTooltip();

/** 探测勾选确认：去重合并进 model 列表（精确 Ordinal 匹配），走正常保存链。 */
function onProbeConfirm(names: string[]): void {
  const p = selected.value;
  if (!p) return;
  const have = new Set(p.models.map((m) => m.id));
  let added = 0;
  for (const name of names) {
    const id = (name ?? '').trim();
    if (!id || have.has(id)) continue;
    p.models.push({ id, displayName: '', protocol: 'chat', contextWindow: null, maxTokens: null, acceptsImages: false, includeReasoningHistory: true, expanded: true });
    have.add(id);
    added += 1;
  }
  markDirty();
  toast.success(added > 0 ? `已加入 ${added} 个（未保存）` : '所选均已在列表');
}

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const s = (await transport.request('settings.get', null)) as {
      activeProvider?: string; activeModel?: string; migrationRequired?: boolean;
      providers?: unknown; proxy?: { mode?: string };
    };
    const t = normalizeProvidersTree(s);
    t.providers.forEach((p, i) => { p.expanded = i === 0; p.models.forEach((m, j) => { m.expanded = j === 0; }); });
    tree.value = t;
    migrationRequired.value = s.migrationRequired === true;
    globalMode.value = s.proxy?.mode ?? 'auto';
    const firstActive = t.providers.find((p) => p.id === t.activeProvider)?.id ?? t.providers[0]?.id ?? '';
    selectedId.value = firstActive;
    newProviderMode.value = false;
    newProviderId.value = '';
    confirmOverwrite.value = false;
    originalJson.value = JSON.stringify(snapshotForDirty());
    fieldErrors.value = {};
    try {
      const snap = (await transport.request('state.snapshot', null)) as { agent?: { busy?: boolean } };
      busy.value = snap.agent?.busy === true;
    } catch { busy.value = false; }
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

function selectProvider(id: string): void {
  if (dirty.value) {
    if (!window.confirm('有未保存的改动，切换将丢失？')) return;
    resetToSaved();
  }
  selectedId.value = id;
  newProviderMode.value = false;
}

function startNewProvider(): void {
  if (dirty.value && !window.confirm('有未保存的改动，继续将丢失？')) return;
  newProviderMode.value = true;
  newProviderId.value = '';
  selectedId.value = '';
  markDirty();
}

function confirmNewProvider(): void {
  const id = newProviderId.value.trim();
  if (!/^[a-z0-9][a-z0-9-_]{0,63}$/.test(id)) {
    toast.warning('注册名称须匹配 ^[a-z0-9][a-z0-9-_]{0,63}$');
    return;
  }
  if (tree.value.providers.some((p) => p.id === id)) {
    toast.warning('该注册名称已存在');
    return;
  }
  tree.value.providers.push({
    id, displayName: '', baseUrl: '', apiKeySet: false, newKey: '', clearKey: false,
    headersEnabled: true, headers: [], useProxy: true, models: [], expanded: true,
  });
  selectedId.value = id;
  newProviderMode.value = false;
  newProviderId.value = '';
  markDirty();
}

function addModel(): void {
  const p = selected.value;
  if (!p) return;
  p.models.push({
    id: '', displayName: '', protocol: 'chat', contextWindow: null, maxTokens: null,
    acceptsImages: false, includeReasoningHistory: true, expanded: true,
  });
  markDirty();
}

function deleteModel(pid: string, mid: string): void {
  pendingDeleteModel.value = { pid, mid };
}

function confirmDeleteModel(): void {
  const d = pendingDeleteModel.value;
  pendingDeleteModel.value = null;
  if (!d) return;
  const p = tree.value.providers.find((x) => x.id === d.pid);
  if (!p) return;
  p.models = p.models.filter((m) => m.id !== d.mid);
  markDirty();
  toast.success('已删除模型（未保存）');
}

function confirmDeleteProvider(): void {
  showDeleteProvider.value = false;
  const p = selected.value;
  if (!p) return;
  const hitActive = tree.value.activeProvider === p.id;
  tree.value.providers = tree.value.providers.filter((x) => x.id !== p.id);
  if (hitActive) {
    tree.value.activeProvider = '';
    tree.value.activeModel = '';
  }
  selectedId.value = tree.value.providers[0]?.id ?? '';
  markDirty();
  toast.success('已删除 Provider（未保存）');
}

function toSavePayloadWithForce(force: boolean): Record<string, unknown> {
  const providers: Record<string, unknown> = {};
  for (const p of tree.value.providers) {
    providers[p.id] = {
      displayName: p.displayName || undefined,
      baseUrl: p.baseUrl,
      apiKey: p.newKey || undefined,
      clearKey: p.clearKey || undefined,
      headersEnabled: p.headersEnabled,
      headers: p.headers.filter((h) => h.key.trim() !== ''),
      useProxy: p.useProxy,
      models: p.models.map((m) => ({
        id: m.id,
        displayName: m.displayName || undefined,
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


/** 两步确认：migrationRequired 时第一次点击仅进入待确认态，第二次才带 force 保存。 */
function onSaveClick(): void {
  if (migrationRequired.value && !confirmOverwrite.value) {
    confirmOverwrite.value = true;
    toast.warning('旧配置文件已自动备份，确认用当前整树覆盖请再点一次保存');
    return;
  }
  void save(migrationRequired.value && confirmOverwrite.value);
}

async function save(force = false): Promise<void> {
  if (saving.value) return;
  const errs = validateProvidersTree({
    activeProvider: tree.value.activeProvider,
    activeModel: tree.value.activeModel,
    providers: tree.value.providers.map((p) => ({
      id: p.id, displayName: p.displayName, baseUrl: p.baseUrl, apiKeySet: p.apiKeySet,
      newKey: p.newKey, clearKey: p.clearKey, headersEnabled: p.headersEnabled,
      headers: p.headers, useProxy: p.useProxy, models: p.models,
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
      ok?: boolean; warnings?: string[];
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
    confirmOverwrite.value = false;
    toast.success('已保存');
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
  } finally {
    saving.value = false;
  }
}

async function setActive(pid: string, mid: string): Promise<void> {
  if (dirty.value) {
    toast.warning('有未保存的改动，请先保存再设 active');
    return;
  }
  if (busy.value) {
    toast.warning('有回合正在执行，切换被拒绝');
    return;
  }
  activating.value = true;
  try {
    await transport.request('settings.setActive', { providerId: pid, modelId: mid });
    tree.value.activeProvider = pid;
    tree.value.activeModel = mid;
    toast.success(`已切换 active：${pid}/${mid}`);
  } catch (e) {
    toast.error(`切换失败：${(e as Error).message}`);
  } finally {
    activating.value = false;
  }
}

function isDirty(): boolean { return dirty.value; }
function resetToSaved(): void { void load(); }
defineExpose({ isDirty, resetToSaved });
onMounted(() => void load());
</script>

<template>
  <div class="page">
    <h2 class="page-title">模型</h2>
    <div v-if="migrationRequired" class="migrate" role="alert">
      配置结构已升级（旧文件已自动备份），请重建 providers 后保存。
    </div>
    <div v-if="loading" class="page-state"><HSpinner size="sm" /><span>加载中…</span></div>
    <div v-else-if="loadError" class="page-state err" role="alert">
      <span>读取设置失败：{{ loadError }}</span>
      <HButton size="sm" variant="secondary" @click="load">重试</HButton>
    </div>
    <template v-else>
      <div class="cols">
        <aside class="left">
          <div
            v-for="p in tree.providers"
            :key="p.id"
            class="prov-row"
            :class="{ active: p.id === selectedId, isActive: p.id === tree.activeProvider }"
            @click="selectProvider(p.id)"
          >
            <span class="prov-main">{{ p.displayName || p.id }}</span>
            <span class="prov-sub">{{ p.displayName ? p.id : '' }}</span>
            <span class="badge">{{ p.models.length }}</span>
            <span v-if="p.id === tree.activeProvider" class="active-dot" title="active">●</span>
          </div>
          <HButton size="sm" variant="secondary" @click="startNewProvider">+ 新建 Provider</HButton>
        </aside>
        <section class="right">
          <template v-if="newProviderMode">
            <SettingRow compact label="注册名称" desc="不可改（改名=新建+删旧），正则 ^[a-z0-9][a-z0-9-_]{0,63}$" required>
              <HInput :model-value="newProviderId" placeholder="如 deepseek" @update:model-value="(v: string) => (newProviderId = v)" />
            </SettingRow>
            <div class="row-actions">
              <HButton size="sm" variant="primary" @click="confirmNewProvider">创建</HButton>
              <HButton size="sm" variant="ghost" @click="newProviderMode = false">取消</HButton>
            </div>
          </template>
           <template v-else-if="selected">
             <section class="prov-group">
               <SettingRow compact label="注册名称" desc="不可改"><HInput :model-value="selected.id" :disabled="true" @update:model-value="() => {}" /></SettingRow>
               <SettingRow compact label="别称" desc="展示用，可空回落 id"><HInput :model-value="selected.displayName" placeholder="回落 id" @update:model-value="(v: string) => { selected!.displayName = v; markDirty(); }" /></SettingRow>
            <SettingRow compact label="接口地址" desc="绝对 http(s)，保存时 Trim 并去尾 /" required :error="fieldErrors[`${selected.id}/-.baseUrl`] ?? null">
              <HInput :model-value="selected.baseUrl" placeholder="https://" @update:model-value="(v: string) => { selected!.baseUrl = v; markDirty(); }" />
            </SettingRow>
            <SettingRow compact label="API Key" desc="保存后不再回显；留空=不改">
              <SecretField v-model="selected.newKey" :api-key-set="selected.apiKeySet" @update:model-value="markDirty" />
              <HButton v-if="selected.apiKeySet" size="sm" variant="ghost" @click="selected!.clearKey = !selected!.clearKey; markDirty()">{{ selected.clearKey ? '取消清除' : '清除 API Key' }}</HButton>
            </SettingRow>
            <SettingRow compact label="使用代理" desc="连接模型时走不走全局代理；全局 direct 下置灰">
              <HSwitch :model-value="selected.useProxy" label="使用代理" :disabled="globalMode === 'direct'" @update:model-value="(v: boolean) => { selected!.useProxy = v; markDirty(); }" />
              <span v-if="globalMode === 'direct'" class="hint">全局 direct 下无可用代理</span>
            </SettingRow>
            <SettingRow compact label="自定义请求头" desc="该 Provider 专属" :tooltip="headersTooltip">
              <HSwitch :model-value="selected.headersEnabled" label="自定义请求头开关" @update:model-value="(v: boolean) => { selected!.headersEnabled = v; markDirty(); }" />
              <HeadersEditor :model-value="selected.headers as HeaderRow[]" @update:model-value="(v: HeaderRow[]) => { selected!.headers = v; markDirty(); }" />
            </SettingRow>
            <SettingRow compact label="探测模型" desc="/models 探活（带头、走对代理）">
              <ModelProbe :base-url="selected.baseUrl" :api-key="selected.newKey" :provider-id="selected.id" :headers="selected.headers as HeaderRow[]" :use-proxy="selected.useProxy" :clear-key="selected.clearKey" :existing-models="selected.models.map((m) => m.id)" @probe-confirm="onProbeConfirm" />
             </SettingRow>
             </section>
            <div class="row-actions">
              <HButton size="sm" variant="ghost" @click="showDeleteProvider = true">删除该 Provider</HButton>
            </div>
            <div class="models-head">
              <h3>模型（{{ selected.models.length }}）</h3>
              <HButton size="sm" variant="secondary" @click="addModel">+ 添加模型</HButton>
            </div>
            <div v-for="m in selected.models" :key="m.id || Math.random()" class="model-card">
              <button class="model-head" @click="m.expanded = !m.expanded">
                <span>{{ m.id || '(未命名)' }}</span>
                <span class="prov-sub">{{ m.displayName }}</span>
                <span class="tag">{{ m.protocol }}</span>
                <span v-if="selected.id === tree.activeProvider && m.id === tree.activeModel" class="active-dot">●</span>
              </button>
              <div v-if="m.expanded" class="model-body">
                <SettingRow compact label="实际请求名称" desc="发给 /chat/completions 的 model 字段" required>
                  <HInput :model-value="m.id" placeholder="如 deepseek-chat" @update:model-value="(v: string) => { m.id = v; markDirty(); }" />
                </SettingRow>
                <SettingRow compact label="协议" desc="当前仅 chat 可用">
                  <HSelect :model-value="m.protocol" @update:model-value="(v: string) => { m.protocol = v; markDirty(); }">
                    <option value="chat">chat</option>
                    <option value="responses" disabled title="Responses 协议即将支持，当前仅 chat 可用">responses（即将支持）</option>
                  </HSelect>
                </SettingRow>
                <SettingRow compact label="别称"><HInput :model-value="m.displayName" @update:model-value="(v: string) => { m.displayName = v; markDirty(); }" /></SettingRow>
                <SettingRow compact label="上下文最大值" desc="空=未知；点预设或手动输入">
                  <div class="ctx-row">
                    <HSwitch :model-value="m.contextWindow !== null" label="启用上下文" @update:model-value="(v: boolean) => { m.contextWindow = v ? 128000 : null; markDirty(); }" />
                    <div v-if="m.contextWindow !== null" class="num-wrap">
                      <HInput :model-value="String(m.contextWindow)" type="number" placeholder="如 128000" @update:model-value="(v: string) => { m.contextWindow = Math.trunc(Number(v)) || null; markDirty(); }" />
                      <div class="preset-group" role="group" aria-label="上下文预设">
                        <HButton v-for="preset in CTX_PRESETS" :key="preset.value" size="sm" :variant="m.contextWindow === preset.value ? 'primary' : 'secondary'" :aria-pressed="m.contextWindow === preset.value" @click="m.contextWindow = preset.value; markDirty()">{{ preset.label }}</HButton>
                      </div>
                    </div>
                    <span v-else class="hint">未知（不做配额估算）</span>
                  </div>
                </SettingRow>
                <SettingRow compact label="输出 token 最大值" desc="可空=不限制；点预设或手动输入">
                  <div class="ctx-row">
                    <HSwitch :model-value="m.maxTokens !== null" label="启用输出上限" @update:model-value="(v: boolean) => { m.maxTokens = v ? 5000 : null; markDirty(); }" />
                    <div v-if="m.maxTokens !== null" class="num-wrap">
                      <HInput :model-value="String(m.maxTokens)" type="number" placeholder="如 20000" @update:model-value="(v: string) => { m.maxTokens = Math.trunc(Number(v)) || null; markDirty(); }" />
                      <div class="preset-group" role="group" aria-label="输出上限预设">
                        <HButton v-for="preset in MAXTOK_PRESETS" :key="preset.value" size="sm" :variant="m.maxTokens === preset.value ? 'primary' : 'secondary'" :aria-pressed="m.maxTokens === preset.value" @click="m.maxTokens = preset.value; markDirty()">{{ preset.label }}</HButton>
                      </div>
                    </div>
                    <span v-else class="hint">不限制（直通 max_tokens）</span>
                  </div>
                </SettingRow>
                <SettingRow compact label="图片" desc="false 时前端禁贴图、后端硬拦"><HSwitch :model-value="m.acceptsImages" label="图片开关" @update:model-value="(v: boolean) => { m.acceptsImages = v; markDirty(); }" /></SettingRow>
                <SettingRow compact label="携带历史 reasoning" desc="默认开；关则发送侧 strip"><HSwitch :model-value="m.includeReasoningHistory" label="携带历史 reasoning" @update:model-value="(v: boolean) => { m.includeReasoningHistory = v; markDirty(); }" /></SettingRow>
                <div class="row-actions">
                  <HButton size="sm" variant="ghost" @click="deleteModel(selected.id, m.id)">删除模型</HButton>
                  <HButton
                    size="sm" variant="secondary"
                    :disabled="dirty || busy || !m.id"
                    :loading="activating"
                    @click="setActive(selected.id, m.id)"
                  >设为 active</HButton>
                </div>
              </div>
            </div>
          </template>
          <p v-else class="hint">暂无 Provider，请新建。</p>
        </section>
      </div>
      <div class="page-actions">
        <span class="hint">全局代理：{{ globalMode }}</span>
        <HButton :variant="migrationRequired && confirmOverwrite ? 'danger' : 'primary'" :disabled="!dirty" :loading="saving" @click="onSaveClick">{{ migrationRequired && confirmOverwrite ? '确认覆盖旧配置并保存' : '保存 providers' }}</HButton>
        <HButton variant="secondary" :disabled="!dirty" @click="resetToSaved">放弃</HButton>
        <span v-if="dirty" class="dirty-hint" role="status">有未保存的改动（dirty 时禁用设 active）</span>
        <span v-if="migrationRequired" class="hint">旧配置已备份，保存需点两次确认覆盖</span>
      </div>
    </template>
  </div>
  <HDialog :open="showDeleteProvider" title="删除 Provider？" @close="showDeleteProvider = false">
    <p>将删除 {{ selected?.id }} 及其下全部模型（未保存）。若命中 active，需先选新 active 或置未配置态。</p>
    <template #footer>
      <HButton size="sm" variant="ghost" @click="showDeleteProvider = false">取消</HButton>
      <HButton size="sm" variant="primary" @click="confirmDeleteProvider">删除</HButton>
    </template>
  </HDialog>
  <HDialog :open="pendingDeleteModel !== null" title="删除模型？" @close="pendingDeleteModel = null">
    <p>将删除该模型（未保存）。若命中 active，需先选新 active。</p>
    <template #footer>
      <HButton size="sm" variant="ghost" @click="pendingDeleteModel = null">取消</HButton>
      <HButton size="sm" variant="primary" @click="confirmDeleteModel">删除</HButton>
    </template>
  </HDialog>
</template>

<style scoped>
.page { max-width: none; }
.page-title { font-size: var(--text-lg); font-weight: 600; margin: 0 0 var(--space-3); }
.migrate { padding: var(--space-2) var(--space-3); background: hsl(var(--warning-subtle)); color: hsl(var(--warning)); border-radius: var(--radius-sm); margin-bottom: var(--space-3); font-size: var(--text-sm); }
.page-state { display: flex; align-items: center; gap: var(--space-2); color: hsl(var(--fg-muted)); font-size: var(--text-sm); padding: var(--space-4) 0; }
.page-state.err { color: hsl(var(--danger)); flex-direction: column; align-items: flex-start; }
.cols { display: flex; gap: var(--space-3); }
.left { width: 200px; flex-shrink: 0; display: flex; flex-direction: column; gap: var(--space-1); }
.prov-row { display: flex; align-items: center; gap: var(--space-2); padding: var(--space-1-5) var(--space-2); border-radius: var(--radius-sm); cursor: pointer; border: 1px solid transparent; }
.prov-row:hover { background: hsl(var(--bg-muted)); }
.prov-row.active { border-color: hsl(var(--primary)); }
.prov-main { font-weight: 600; overflow: hidden; text-overflow: ellipsis; }
.prov-sub { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); overflow: hidden; text-overflow: ellipsis; }
.badge { margin-left: auto; font-size: var(--text-xs); background: hsl(var(--bg-muted)); padding: 0 var(--space-1); border-radius: var(--radius-sm); }
.active-dot { color: hsl(var(--primary)); }
.right { flex: 1; min-width: 0; }
.models-head { display: flex; align-items: center; justify-content: space-between; margin: var(--space-3) 0 var(--space-1-5); }
.model-card { border: 1px solid hsl(var(--border-subtle)); border-radius: var(--radius-md); margin-bottom: var(--space-1-5); }
.model-head { width: 100%; display: flex; align-items: center; gap: var(--space-2); padding: var(--space-1-5) var(--space-2); background: none; border: none; cursor: pointer; font-weight: 600; }
.tag { font-size: var(--text-xs); background: hsl(var(--bg-muted)); padding: 0 var(--space-1); border-radius: var(--radius-sm); }
.model-body { padding: 0 var(--space-2) var(--space-2); }
.row-actions { display: flex; gap: var(--space-2); margin: var(--space-2) 0; }
.ctx-row { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
.num-wrap { display: flex; flex-direction: column; gap: var(--space-1-5); min-width: 0; }
.preset-group { display: flex; flex-wrap: wrap; gap: var(--space-1); }
.ctx-row > :last-child { flex: 1; min-width: 0; }
.model-body, .prov-group { min-width: 0; }
section.prov-group { border: 1px solid hsl(var(--border-subtle)); border-radius: var(--radius-md); padding: var(--space-2) var(--space-3); min-width: 0; }
.hint { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); }
.page-actions { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-3) 0; position: sticky; bottom: 0; background: hsl(var(--bg)); border-top: 1px solid hsl(var(--border-subtle)); }
.dirty-hint { font-size: var(--text-xs); color: hsl(var(--warning)); }
</style>
