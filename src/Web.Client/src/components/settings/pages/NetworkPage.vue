<!-- src/Web.Client/src/components/settings/pages/NetworkPage.vue -->
<!-- P22：全局三档 system/direct/manual + 各 Provider effective 一览 + 当前生效 + 一键探测（retry 已搬家至行为页）。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import HInput from '@/components/base/HInput.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HRadioGroup from '@/components/base/HRadioGroup.vue';
import HRadio from '@/components/base/HRadio.vue';
import { validateNetworkForm } from '../validators';
import { hasAlias, mainName } from '../providerEditing';

const toast = useToast();
type ProxyMode = 'system' | 'direct' | 'manual';
type ProviderNetwork = 'follow' | 'proxy' | 'direct';
interface ProviderEffective {
  inUse: boolean;
  source: string;
  maskedUrl: string | null;
  warning: string | null;
}
const loading = ref(true);
const saving = ref(false);
const loadError = ref<string | null>(null);
const mode = ref<ProxyMode>('system');
const url = ref('');
const providers = ref<Array<{
  id: string;
  displayName: string;
  hasAlias: boolean;
  network: ProviderNetwork;
  baseUrl: string;
  effective: ProviderEffective | null;
}>>([]);
const saveWarnings = ref<string[]>([]);
const probing = ref<string | null>(null);
const probeInfo = ref<Record<string, string>>({});
const original = ref({ mode: 'system' as ProxyMode, url: '' });
const fieldErrors = ref<Record<string, string>>({});

const dirty = computed(() => mode.value !== original.value.mode || url.value !== original.value.url);

function markDirty(): void { fieldErrors.value = {}; }

function normalizeMode(m: unknown): ProxyMode {
  const v = String(m ?? 'system').toLowerCase();
  if (v === 'manual') return 'manual';
  if (v === 'direct' || v === 'auto') return 'direct';
  return 'system';
}

function normalizeNetwork(n: unknown, legacy?: unknown): ProviderNetwork {
  if (n === 'proxy' || n === 'direct' || n === 'follow') return n;
  if (legacy === false) return 'direct';
  return 'follow';
}

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const s = (await transport.request('settings.get', null)) as {
      proxy?: { mode?: string; url?: string | null };
      providers?: Array<{
        id?: string;
        displayName?: string;
        network?: string;
        useProxy?: boolean;
        baseUrl?: string;
        effective?: ProviderEffective | null;
      }>;
    };
    mode.value = normalizeMode(s.proxy?.mode);
    url.value = typeof s.proxy?.url === 'string' ? s.proxy.url : '';
    providers.value = (s.providers ?? []).map((p) => ({
      id: p.id ?? '',
      displayName: mainName(p.id ?? '', p.displayName ?? ''),
      hasAlias: hasAlias(p.id ?? '', p.displayName ?? ''),
      network: normalizeNetwork(p.network, p.useProxy),
      baseUrl: p.baseUrl ?? '',
      effective: p.effective && typeof p.effective.inUse === 'boolean' ? {
        inUse: p.effective.inUse,
        source: String(p.effective.source ?? ''),
        maskedUrl: typeof p.effective.maskedUrl === 'string' ? p.effective.maskedUrl : null,
        warning: typeof p.effective.warning === 'string' ? p.effective.warning : null,
      } : null,
    })).filter((p) => p.id);
    original.value = { mode: mode.value, url: url.value };
    fieldErrors.value = {};
    saveWarnings.value = [];
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

async function save(): Promise<void> {
  if (saving.value) return;
  const errs = validateNetworkForm({ mode: mode.value, url: url.value });
  const map: Record<string, string> = {};
  for (const e of errs) map[e.field] = e.message;
  fieldErrors.value = map;
  if (errs.length > 0) {
    toast.warning('请先修正表单中的错误');
    return;
  }
  saving.value = true;
  try {
    const r = (await transport.request('settings.saveNetwork', {
      mode: mode.value,
      url: mode.value === 'manual' ? url.value : undefined,
    })) as { warnings?: string[] };
    original.value = { mode: mode.value, url: url.value };
    saveWarnings.value = Array.isArray(r.warnings) ? r.warnings : [];
    for (const w of saveWarnings.value) toast.warning(w);
    toast.success('已保存');
    await load();
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
  } finally {
    saving.value = false;
  }
}

/** 一键探测：复用 probeModels，显示 via + latencyMs。 */
async function probeProvider(id: string): Promise<void> {
  const p = providers.value.find((x) => x.id === id);
  if (!p || probing.value) return;
  probing.value = id;
  try {
    const r = (await transport.request('settings.probeModels', {
      baseUrl: p.baseUrl,
      providerId: p.id,
      network: p.network,
    })) as {
      models?: string[];
      via?: { source?: string; maskedUrl?: string | null };
      latencyMs?: number;
    };
    const via = `via=${r.via?.source ?? '?'}${r.via?.maskedUrl ? `(${r.via.maskedUrl})` : ''}`;
    const lat = typeof r.latencyMs === 'number' ? ` · ${r.latencyMs}ms` : '';
    probeInfo.value = { ...probeInfo.value, [id]: `${via}${lat} · ${(r.models ?? []).length} 个模型` };
  } catch (e) {
    probeInfo.value = { ...probeInfo.value, [id]: `探测失败：${(e as Error).message}` };
  } finally {
    probing.value = null;
  }
}

function effectiveTag(p: (typeof providers.value)[number]): string {
  if (p.effective) {
    if (p.effective.inUse)
      return p.effective.source === 'global-system'
        ? '跟随系统走代理'
        : `走代理 ${p.effective.maskedUrl ?? ''}`.trim();
    return p.effective.source === 'bypass-local' ? '本地直连' : '直连';
  }
  return p.network === 'follow' ? '跟随全局' : p.network === 'proxy' ? '该服务走代理' : '该服务直连';
}

function isDirty(): boolean { return dirty.value; }
function resetToSaved(): void {
  mode.value = original.value.mode;
  url.value = original.value.url;
  fieldErrors.value = {};
}
defineExpose({ isDirty, resetToSaved });
onMounted(() => void load());
</script>

<template>
  <div class="page">
    <h2 class="page-title">网络</h2>
    <div v-if="loading" class="page-state"><HSpinner size="sm" /><span>加载中…</span></div>
    <div v-else-if="loadError" class="page-state err" role="alert">
      <span>读取设置失败：{{ loadError }}</span>
      <HButton size="sm" variant="secondary" @click="load">重试</HButton>
    </div>
    <template v-else>
      <SettingRow label="代理模式" desc="国内服务直连更快，国外服务走代理才可用。全局决定走哪条路，单个服务可在“模型”页单独覆盖">
        <HRadioGroup :model-value="mode" name="proxy-mode" direction="horizontal" @update:model-value="(v: string | number) => { mode = v === 'manual' ? 'manual' : v === 'direct' ? 'direct' : 'system'; markDirty(); }">
          <HRadio value="system" label="跟随系统" />
          <HRadio value="direct" label="强制直连" />
          <HRadio value="manual" label="走指定地址" />
        </HRadioGroup>
      </SettingRow>
      <SettingRow v-if="mode === 'system'" label="当前生效" desc="与浏览器/系统设置一致；无系统代理时自动直连">
        <span class="tag">跟随系统</span>
      </SettingRow>
      <SettingRow v-if="mode === 'manual'" label="代理地址" desc="仅指定地址模式需要，http(s):// 开头" required :error="fieldErrors['url'] ?? null">
        <HInput :model-value="url" placeholder="http://127.0.0.1:7890" :error="!!fieldErrors['url']" @update:model-value="(v: string) => { url = v; markDirty(); }" />
      </SettingRow>
      <div v-if="saveWarnings.length > 0" class="warn-box" role="status">
        <p v-for="(w, i) in saveWarnings" :key="i" class="warn-line">{{ w }}</p>
      </div>
      <SettingRow label="各服务生效" desc="全局决定走哪条路，单个服务可单独覆盖；本地/内网地址永远直连">
        <div class="prov">
          <p v-for="p in providers" :key="p.id" class="prov-row">
            <span>{{ p.displayName }}</span>
            <span v-if="p.hasAlias" class="prov-sub">{{ p.id }}</span>
            <span class="tag">{{ effectiveTag(p) }}</span>
            <span class="prov-sub">{{ p.network === 'follow' ? '跟随全局' : p.network === 'proxy' ? '走代理' : '直连' }}</span>
            <HButton size="sm" variant="secondary" :loading="probing === p.id" @click="probeProvider(p.id)">探测</HButton>
            <span v-if="probeInfo[p.id]" class="prov-sub">{{ probeInfo[p.id] }}</span>
            <span v-if="p.effective?.warning" class="warn-line">{{ p.effective.warning }}</span>
          </p>
          <p v-if="providers.length === 0" class="prov-empty">还没有 Provider（先在“模型”页配置）。</p>
        </div>
      </SettingRow>
      <p class="fixed-note">全局决定走哪条路，单个服务可单独覆盖；本地/内网地址永远直连；“该服务走代理”但全局无可用代理时降级直连并出黄条（非静默）。</p>
      <div class="page-actions">
        <HButton variant="primary" :disabled="!dirty" :loading="saving" @click="save">保存</HButton>
        <span v-if="dirty" class="dirty-hint" role="status">有未保存的改动</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.page {
  max-width: var(--settings-page-max, 860px);
}
.page-title {
  font-size: var(--text-base);
  font-weight: 600;
  margin: 0 0 var(--space-2);
}
.page-state { display: flex; align-items: center; gap: var(--space-2); color: hsl(var(--fg-muted)); font-size: var(--text-sm); padding: var(--space-4) 0; }
.page-state.err { color: hsl(var(--danger)); flex-direction: column; align-items: flex-start; }
.prov { display: flex; flex-direction: column; gap: var(--space-1); }
.prov-row { display: flex; gap: var(--space-2); align-items: center; font-size: var(--text-sm); flex-wrap: wrap; }
.prov-sub { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); }
.tag { font-size: var(--text-xs); background: hsl(var(--bg-muted)); padding: 0 var(--space-1); border-radius: var(--radius-sm); }
.prov-empty { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); }
.warn-box { border: 1px solid hsl(var(--warning)); border-radius: var(--radius-sm); padding: var(--space-2); margin: var(--space-2) 0; }
.warn-line { font-size: var(--text-xs); color: hsl(var(--warning)); }
.fixed-note { font-size: var(--text-xs); color: hsl(var(--fg-muted)); margin: var(--space-2) 0; }
.page-actions { display: flex; align-items: center; gap: var(--space-2); padding: var(--space-2) 0; position: sticky; bottom: 0; background: hsl(var(--bg)); border-top: 1px solid hsl(var(--border-subtle)); }
.dirty-hint { font-size: var(--text-xs); color: hsl(var(--warning)); }
</style>
