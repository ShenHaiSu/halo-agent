<!-- src/Web.Client/src/components/settings/pages/ModelPage.vue -->
<!-- P14：单列嵌套手风琴（ProviderCard > ProviderHeader + ProviderBody 含 ModelList），Model 永为 Provider 子节点。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import SecretField from '../fields/SecretField.vue';
import HeadersEditor, { type HeaderRow } from '../fields/HeadersEditor.vue';
import ModelProbe from '../fields/ModelProbe.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HInput from '@/components/base/HInput.vue';
import HRadioGroup from '@/components/base/HRadioGroup.vue';
import HRadio from '@/components/base/HRadio.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HSwitch from '@/components/base/HSwitch.vue';
import HDialog from '@/components/base/HDialog.vue';
import { headerPlaceholderTooltip } from '../fields/headerPlaceholders';
import { hasAlias, mainName, normalizeProvidersTree, validateProvidersTree, type ProvidersTree } from '../providerEditing';
import { useSessionStore } from '@/stores/session';

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
const newProviderMode = ref(false);
const newProviderId = ref('');
const fieldErrors = ref<Record<string, string>>({});
const pendingDeleteProvider = ref<string | null>(null);
/** 方案A：migrationRequired 下保存要点两次确认覆盖（第二次才带 force=true）。 */
const confirmOverwrite = ref(false);
const pendingDeleteModel = ref<{ pid: string; mid: string } | null>(null);

const dirty = computed(() => JSON.stringify(snapshotForDirty()) !== originalJson.value);

/** P14：快照排除 expanded(UI 态)，新建空 Card 纳入 dirty。 */
function snapshotForDirty(): unknown {
  return {
    newProviderMode: newProviderMode.value,
    newProviderId: newProviderId.value,
    providers: tree.value.providers.map((p) => ({
      id: p.id, displayName: p.displayName, baseUrl: p.baseUrl,
      newKey: p.newKey, clearKey: p.clearKey, headersEnabled: p.headersEnabled,
      headers: p.headers, useProxy: p.useProxy,
      models: p.models.map((m) => ({
        id: m.id, displayName: m.displayName, protocol: m.protocol,
        contextWindow: m.contextWindow, maxTokens: m.maxTokens,
        acceptsImages: m.acceptsImages, includeReasoningHistory: m.includeReasoningHistory,
      })),
    })),
  };
}

function markDirty(): void { fieldErrors.value = {}; }

function toggleProvider(id: string): void {
  const p = tree.value.providers.find((x) => x.id === id);
  if (p) p.expanded = !p.expanded;
}

function toggleModel(pid: string, mid: string, idx: number): void {
  const p = tree.value.providers.find((x) => x.id === pid);
  const m = p?.models[idx];
  if (m && m.id === mid) m.expanded = !m.expanded;
}

const headersTooltip = headerPlaceholderTooltip();

function formatCtx(n: number | null): string {
  if (n === null || n === undefined) return '';
  if (n >= 1000000) {
    const m = n / 1000000;
    return `${Number.isInteger(m) ? m : m.toFixed(1)}M`;
  }
  if (n >= 1000) return `${Math.round(n / 1000)}K`;
  return String(n);
}

/** 探测勾选确认：去重合并进 model 列表（精确 Ordinal 匹配），走正常保存链。 */
function onProbeConfirm(pid: string, names: string[]): void {
  const p = tree.value.providers.find((x) => x.id === pid);
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

function startNewProvider(): void {
  if (dirty.value && !window.confirm('有未保存的改动，继续将丢失？')) return;
  newProviderMode.value = true;
  newProviderId.value = '';
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
  newProviderMode.value = false;
  newProviderId.value = '';
  markDirty();
}

function addModel(pid: string): void {
  const p = tree.value.providers.find((x) => x.id === pid);
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
  const pid = pendingDeleteProvider.value;
  pendingDeleteProvider.value = null;
  if (!pid) return;
  const hitActive = tree.value.activeProvider === pid;
  tree.value.providers = tree.value.providers.filter((x) => x.id !== pid);
  if (hitActive) {
    tree.value.activeProvider = '';
    tree.value.activeModel = '';
  }
  markDirty();
  toast.success('已删除 Provider（未保存）');
}

function toSavePayloadWithForce(force: boolean): Record<string, unknown> {
  const providers: Record<string, unknown> = {};
  for (const p of tree.value.providers) {
    providers[p.id] = {
      // P14 缺席/显式空区分：整树下发一律显式传 displayName，空字串=清除（后端 NormalizeAlias→null）。
      displayName: p.displayName,
      baseUrl: p.baseUrl,
      apiKey: p.newKey || undefined,
      clearKey: p.clearKey || undefined,
      headersEnabled: p.headersEnabled,
      headers: p.headers.filter((h) => h.key.trim() !== ''),
      useProxy: p.useProxy,
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
     // P12-02：setActive 无事件推送，成功后主动拉快照同步 session（切换器高亮源），
     // P13：chat/responses 均可设 active（保存校验已放开）。
     try {
       const snap = await transport.request<{ agent?: Record<string, unknown> }>('state.snapshot', null);
       if (snap?.agent && typeof snap.agent === 'object') useSessionStore().applySnapshotAgent(snap.agent);
     } catch {
       /* 快照补齐失败不影响 toast，下次 loadSnapshot 会补 */
     }
     // P14：Toast 双写主名(id)。
     const pd = tree.value.providers.find((x) => x.id === pid);
     const md = pd?.models.find((m) => m.id === mid);
     toast.success(`已切换 active：${mainName(pid, pd?.displayName)}(${pid}) / ${mainName(mid, md?.displayName)}(${mid})`);
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
      <div class="prov-list">
        <section
          v-for="p in tree.providers"
          :key="p.id"
          class="prov-card"
          :class="{ active: p.id === tree.activeProvider }"
        >
          <button
            type="button"
            class="prov-head"
            :aria-expanded="!!p.expanded"
            :aria-label="`${mainName(p.id, p.displayName)}（${p.models.length} 个模型），${p.expanded ? '收起' : '展开'}详情`"
            @click="toggleProvider(p.id)"
          >
            <span v-if="p.id === tree.activeProvider" class="active-dot" aria-label="使用中" />
            <span v-else class="active-dot off" aria-hidden="true" />
            <span class="prov-main">{{ mainName(p.id, p.displayName) }}</span>
            <span v-if="hasAlias(p.id, p.displayName)" class="prov-sub">{{ p.id }}</span>
            <span class="badge">{{ p.models.length }}</span>
            <span class="mini">{{ (p.apiKeySet || p.newKey.trim() !== '') ? 'key已设' : '无key' }}</span>
            <span class="mini">{{ p.useProxy ? (globalMode === 'direct' ? '全局direct下直连' : '走代理') : '直连' }}</span>
            <HaloIcon name="chevron-down" :size="14" class="chev" :class="{ open: !!p.expanded }" aria-hidden="true" />
          </button>
          <div v-if="p.expanded" class="prov-body">
            <div class="addr-key">
              <SettingRow compact label="接口地址" desc="绝对 http(s)，保存时 Trim 并去尾 /；responses 协议在该地址下走 /responses 路径" required :error="fieldErrors[`${p.id}/-.baseUrl`] ?? null">
                <HInput :model-value="p.baseUrl" placeholder="https://" @update:model-value="(v: string) => { p.baseUrl = v; markDirty(); }" />
              </SettingRow>
              <SettingRow compact label="API Key" desc="保存后不再回显；留空=不改">
                <div class="key-row">
                  <SecretField v-model="p.newKey" :api-key-set="p.apiKeySet" @update:model-value="markDirty" />
                  <HButton v-if="p.apiKeySet" size="sm" variant="ghost" @click="p.clearKey = !p.clearKey; markDirty()">{{ p.clearKey ? '取消清除' : '清除 API Key' }}</HButton>
                </div>
              </SettingRow>
            </div>
            <SettingRow compact label="注册名称" desc="不可改（改名=新建+删旧），正则 ^[a-z0-9][a-z0-9-_]{0,63}$"><HInput :model-value="p.id" :disabled="true" @update:model-value="() => {}" /></SettingRow>
            <SettingRow compact label="别称" desc="展示用，可空回落 id；留空=清除"><HInput :model-value="p.displayName" :placeholder="p.id" @update:model-value="(v: string) => { p.displayName = v; markDirty(); }" /></SettingRow>
            <SettingRow compact label="使用代理" desc="连接模型时走不走全局代理；全局 direct 下置灰">
              <div class="inline-row">
                <HSwitch :model-value="p.useProxy" label="使用代理" :disabled="globalMode === 'direct'" @update:model-value="(v: boolean) => { p.useProxy = v; markDirty(); }" />
                <span v-if="globalMode === 'direct'" class="hint">全局 direct 下无可用代理</span>
              </div>
            </SettingRow>
            <SettingRow compact label="自定义请求头" desc="该 Provider 专属" :tooltip="headersTooltip">
              <HSwitch :model-value="p.headersEnabled" label="自定义请求头开关" @update:model-value="(v: boolean) => { p.headersEnabled = v; markDirty(); }" />
              <HeadersEditor :model-value="p.headers as HeaderRow[]" @update:model-value="(v: HeaderRow[]) => { p.headers = v; markDirty(); }" />
            </SettingRow>
            <SettingRow compact label="探测模型" desc="/models 探活（带头、走对代理）">
              <ModelProbe :base-url="p.baseUrl" :api-key="p.newKey" :provider-id="p.id" :headers="p.headers as HeaderRow[]" :use-proxy="p.useProxy" :clear-key="p.clearKey" :existing-models="p.models.map((m) => m.id)" @probe-confirm="(names: string[]) => onProbeConfirm(p.id, names)" />
            </SettingRow>
            <div class="models-head">
              <h3>模型（{{ p.models.length }}）</h3>
              <HButton size="sm" variant="secondary" @click="addModel(p.id)">+ 添加模型</HButton>
            </div>
            <div v-if="p.models.length === 0" class="empty-hint" role="status">该 Provider 下暂无模型，点右上添加。</div>
            <div v-for="(m, mi) in p.models" :key="m.id || `new-${mi}`" class="model-card">
               <div class="model-head" @click="toggleModel(p.id, m.id, mi)">
                 <span class="mmain">{{ mainName(m.id, m.displayName) || '(未命名)' }}</span>
                 <span v-if="hasAlias(m.id, m.displayName)" class="msub">{{ m.id }}</span>
                 <span class="proto-badge" :class="{ resp: m.protocol === 'responses' }">{{ m.protocol === 'responses' ? 'resp' : 'chat' }}</span>
                 <span v-if="m.contextWindow" class="mctx">{{ formatCtx(m.contextWindow) }}</span>
                 <span v-if="m.acceptsImages" class="mimg" aria-label="支持图片">图</span>
                 <span v-if="p.id === tree.activeProvider && m.id === tree.activeModel" class="active-dot" aria-label="使用中">●</span>
                 <HButton
                   size="sm" variant="secondary"
                   :disabled="dirty || busy || !m.id"
                   :loading="activating"
                   @click.stop="setActive(p.id, m.id)"
                 >设为 active</HButton>
                 <button
                   type="button"
                   class="chev-btn"
                   :aria-expanded="!!m.expanded"
                   :aria-label="`${mainName(m.id, m.displayName)}，${m.expanded ? '收起' : '展开'}详情`"
                   @click.stop="toggleModel(p.id, m.id, mi)"
                 ><HaloIcon name="chevron-down" :size="14" class="chev" :class="{ open: !!m.expanded }" aria-hidden="true" /></button>
               </div>
              <div v-if="m.expanded" class="model-body">
                <SettingRow compact label="实际请求名称" desc="发给模型端点的 model 字段（chat 走 /chat/completions，responses 走 /responses）" required>
                  <HInput :model-value="m.id" placeholder="如 deepseek-chat" @update:model-value="(v: string) => { m.id = v; markDirty(); }" />
                </SettingRow>
                <SettingRow compact label="协议" desc="chat 与 responses 均可用；responses 在接口地址下走 /responses 路径">
                  <HRadioGroup :model-value="m.protocol" direction="horizontal" @update:model-value="(v: string | number) => { m.protocol = String(v); markDirty(); }">
                    <HRadio value="chat" label="chat" />
                    <HRadio value="responses" label="responses" />
                  </HRadioGroup>
                </SettingRow>
                <SettingRow compact label="别称" desc="展示用，可空回落实际请求名称；留空=清除"><HInput :model-value="m.displayName" :placeholder="m.id" @update:model-value="(v: string) => { m.displayName = v; markDirty(); }" /></SettingRow>
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
                  <HButton size="sm" variant="ghost" @click="deleteModel(p.id, m.id)">删除模型</HButton>
                </div>
              </div>
            </div>
            <div class="row-actions">
              <HButton size="sm" variant="ghost" @click="pendingDeleteProvider = p.id">删除该 Provider</HButton>
            </div>
          </div>
        </section>
        <section v-if="newProviderMode" class="prov-card creating" aria-label="新建 Provider">
          <SettingRow compact label="注册名称" desc="不可改（改名=新建+删旧），正则 ^[a-z0-9][a-z0-9-_]{0,63}$" required>
            <HInput :model-value="newProviderId" placeholder="如 deepseek" @update:model-value="(v: string) => (newProviderId = v)" />
          </SettingRow>
          <div class="row-actions">
            <HButton size="sm" variant="primary" @click="confirmNewProvider">创建</HButton>
            <HButton size="sm" variant="ghost" @click="newProviderMode = false; newProviderId = ''">取消</HButton>
          </div>
        </section>
        <HButton v-if="!newProviderMode" size="sm" variant="secondary" @click="startNewProvider">+ 新建 Provider</HButton>
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
  <HDialog :open="pendingDeleteProvider !== null" title="删除 Provider？" @close="pendingDeleteProvider = null">
    <p>将删除 {{ pendingDeleteProvider }} 及其下全部模型（未保存）。若命中 active，需先选新 active 或置未配置态。</p>
    <template #footer>
      <HButton size="sm" variant="ghost" @click="pendingDeleteProvider = null">取消</HButton>
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
.page-title { font-size: var(--text-base); font-weight: 600; margin: 0 0 var(--space-2); }
.migrate { padding: var(--space-2) var(--space-3); background: hsl(var(--warning-subtle)); color: hsl(var(--warning)); border-radius: var(--radius-sm); margin-bottom: var(--space-3); font-size: var(--text-sm); }
.page-state { display: flex; align-items: center; gap: var(--space-2); color: hsl(var(--fg-muted)); font-size: var(--text-sm); padding: var(--space-4) 0; }
.page-state.err { color: hsl(var(--danger)); flex-direction: column; align-items: flex-start; }
.prov-list { display: flex; flex-direction: column; gap: var(--space-1-5); }
.prov-card { border: 1px solid hsl(var(--border-subtle)); border-radius: var(--radius-md); margin-bottom: var(--space-1-5); }
.prov-card.active { border-color: hsl(var(--accent)); }
.prov-card.creating { padding: var(--space-1) var(--space-2) var(--space-2); }
.prov-head { width: 100%; min-height: 32px; display: flex; align-items: center; gap: var(--space-1-5); padding: var(--space-0-5) var(--space-1-5); background: none; border: none; border-radius: var(--radius-md); cursor: pointer; min-width: 0; text-align: left; }
.prov-head:hover { background: hsl(var(--bg-muted)); }
.prov-head:focus-visible { outline: 2px solid hsl(var(--accent) / 0.4); outline-offset: 1px; }
.prov-main { font-size: var(--text-sm); font-weight: 600; color: hsl(var(--fg)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.prov-sub { font-family: var(--font-mono); font-size: var(--text-2xs); color: hsl(var(--fg-subtle)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.badge { flex: none; font-size: var(--text-2xs); background: hsl(var(--bg-muted)); color: hsl(var(--fg-muted)); padding: 0 var(--space-1); border-radius: var(--radius-sm); }
.mini { flex: none; font-size: var(--text-2xs); color: hsl(var(--fg-subtle)); background: hsl(var(--bg-subtle)); padding: 0 var(--space-1); border-radius: var(--radius-xs); }
.active-dot { flex: none; width: 6px; height: 6px; border-radius: 50%; background: hsl(var(--accent)); color: hsl(var(--accent)); font-size: 6px; line-height: 1; }
.active-dot.off { background: transparent; visibility: hidden; }
.chev { margin-left: auto; flex: none; color: hsl(var(--fg-subtle)); transition: transform 0.12s ease; }
.chev.open { transform: rotate(180deg); }
.prov-body { padding: 0 var(--space-2) var(--space-1-5); min-width: 0; }
.addr-key { display: grid; grid-template-columns: 1fr; min-width: 0; }
.key-row { display: flex; flex-direction: column; gap: var(--space-1); min-width: 0; }
.inline-row { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; min-width: 0; }
.models-head { display: flex; align-items: center; justify-content: space-between; margin: var(--space-2) 0 var(--space-1); }
.models-head h3 { margin: 0; font-size: var(--text-sm); font-weight: 600; }
.empty-hint { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); padding: var(--space-1) 0; }
.model-card { border: 1px solid hsl(var(--border-subtle)); border-radius: var(--radius-sm); margin-bottom: var(--space-1); margin-left: var(--space-2); min-width: 0; }
 .model-head { width: 100%; min-height: 32px; display: flex; align-items: center; gap: var(--space-1-5); padding: var(--space-0-5) var(--space-1-5); border-radius: var(--radius-sm); cursor: pointer; min-width: 0; }
 .model-head:hover { background: hsl(var(--bg-muted)); }
 .chev-btn { margin-left: auto; flex: none; display: inline-flex; align-items: center; justify-content: center; background: none; border: none; border-radius: var(--radius-xs); padding: var(--space-0-5); cursor: pointer; color: hsl(var(--fg-subtle)); }
 .chev-btn:focus-visible { outline: 2px solid hsl(var(--accent) / 0.4); outline-offset: 1px; }
.mmain { font-size: var(--text-sm); font-weight: 600; color: hsl(var(--fg)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.msub { font-family: var(--font-mono); font-size: var(--text-2xs); color: hsl(var(--fg-subtle)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.proto-badge { flex: none; font-size: var(--text-2xs); background: hsl(var(--bg-muted)); padding: 0 var(--space-1); border-radius: var(--radius-xs); }
.mctx { font-family: var(--font-mono); font-size: var(--text-2xs); color: hsl(var(--fg-subtle)); flex: none; }
.mimg { font-size: var(--text-2xs); color: hsl(var(--accent)); flex: none; }
.model-body { padding: 0 var(--space-2) var(--space-2); min-width: 0; }
.row-actions { display: flex; gap: var(--space-2); margin: var(--space-2) 0 0; }
.ctx-row { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; min-width: 0; }
.num-wrap { display: flex; flex-direction: column; gap: var(--space-1-5); min-width: 0; }
.preset-group { display: flex; flex-wrap: wrap; gap: var(--space-1); }
.ctx-row > :last-child { flex: 1; min-width: 0; }
.hint { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); }
.page-actions { display: flex; align-items: center; gap: var(--space-2); padding: var(--space-2) 0; position: sticky; bottom: 0; background: hsl(var(--bg)); border-top: 1px solid hsl(var(--border-subtle)); flex-wrap: wrap; }
.dirty-hint { font-size: var(--text-xs); color: hsl(var(--warning)); }
@media (min-width: 860px) {
  .addr-key { grid-template-columns: 1fr 1fr; gap: 0 var(--space-2); }
}
@media (max-width: 700px) {
  .model-card { margin-left: 0; }
  .model-head { flex-wrap: wrap; }
  .mmain, .msub, .mctx { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 100%; }
}
</style>
