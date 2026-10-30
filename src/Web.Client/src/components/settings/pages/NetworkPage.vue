<!-- src/Web.Client/src/components/settings/pages/NetworkPage.vue -->
<!-- P10 瘦身：网络页只剩代理模式三选一 + 代理地址 + 重试 + 各 provider 是否走代理一览（只读跳转）。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import HInput from '@/components/base/HInput.vue';
import HSlider from '@/components/base/HSlider.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HRadioGroup from '@/components/base/HRadioGroup.vue';
import HRadio from '@/components/base/HRadio.vue';
import { validateNetworkForm } from '../validators';

const toast = useToast();
type ProxyMode = 'auto' | 'direct' | 'manual';
const loading = ref(true);
const saving = ref(false);
const loadError = ref<string | null>(null);
const mode = ref<ProxyMode>('auto');
const url = ref('');
const retry = ref(0);
const providers = ref<Array<{ id: string; displayName: string; useProxy: boolean; baseUrl: string }>>([]);
const original = ref({ mode: 'auto' as ProxyMode, url: '', retry: 0 });
const fieldErrors = ref<Record<string, string>>({});

const dirty = computed(() => mode.value !== original.value.mode || url.value !== original.value.url || retry.value !== original.value.retry);

function markDirty(): void { fieldErrors.value = {}; }

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const s = (await transport.request('settings.get', null)) as {
      proxy?: { mode?: string; url?: string | null };
      retry?: number;
      providers?: Array<{ id?: string; displayName?: string; useProxy?: boolean; baseUrl?: string }>;
    };
    const m = String(s.proxy?.mode ?? 'auto').toLowerCase();
    mode.value = m === 'manual' ? 'manual' : m === 'direct' ? 'direct' : 'auto';
    url.value = typeof s.proxy?.url === 'string' ? s.proxy.url : '';
    retry.value = typeof s.retry === 'number' ? s.retry : 0;
    providers.value = (s.providers ?? []).map((p) => ({
      id: p.id ?? '',
      displayName: p.displayName ?? p.id ?? '',
      useProxy: p.useProxy !== false,
      baseUrl: p.baseUrl ?? '',
    })).filter((p) => p.id);
    original.value = { mode: mode.value, url: url.value, retry: retry.value };
    fieldErrors.value = {};
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

async function save(): Promise<void> {
  if (saving.value) return;
  const errs = validateNetworkForm({ mode: mode.value, url: url.value, retry: retry.value });
  const map: Record<string, string> = {};
  for (const e of errs) map[e.field] = e.message;
  fieldErrors.value = map;
  if (errs.length > 0) {
    toast.warning('请先修正表单中的错误');
    return;
  }
  saving.value = true;
  try {
    await transport.request('settings.saveNetwork', {
      mode: mode.value,
      url: mode.value === 'manual' ? url.value : undefined,
      retry: retry.value,
    });
    original.value = { mode: mode.value, url: url.value, retry: retry.value };
    toast.success('已保存');
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
  } finally {
    saving.value = false;
  }
}

function isDirty(): boolean { return dirty.value; }
function resetToSaved(): void {
  mode.value = original.value.mode;
  url.value = original.value.url;
  retry.value = original.value.retry;
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
      <SettingRow label="代理模式" desc="全局代理服务器（供探活/下载/抓网页复用）；模型是否走代理看各 Provider 的 useProxy">
        <HRadioGroup :model-value="mode" name="proxy-mode" direction="horizontal" @update:model-value="(v: string | number) => { mode = v === 'manual' ? 'manual' : v === 'direct' ? 'direct' : 'auto'; markDirty(); }">
          <HRadio value="direct" label="直连" />
          <HRadio value="auto" label="直连（auto 钉死）" />
          <HRadio value="manual" label="自定义" />
        </HRadioGroup>
      </SettingRow>
      <SettingRow label="协议" desc="chat 与 responses 均可用；responses 在各接口地址下走 /responses 路径">
        <div class="prov-proto">
          <span class="proto-badge">chat</span>
          <span class="proto-badge resp">resp</span>
          <span class="hint">模型协议在“模型”页按模型设置（radio），此处仅展示两种协议均已放行</span>
        </div>
      </SettingRow>
      <SettingRow v-if="mode === 'manual'" label="代理地址" desc="仅自定义模式需要，http(s):// 开头" required :error="fieldErrors['url'] ?? null">
        <HInput :model-value="url" placeholder="http://127.0.0.1:7890" :error="!!fieldErrors['url']" @update:model-value="(v: string) => { url = v; markDirty(); }" />
      </SettingRow>
      <SettingRow label="重试次数" desc="请求失败时最多重试次数，0 ~ 8" :error="fieldErrors['retry'] ?? null">
        <HSlider :model-value="retry" :min="0" :max="8" :step="1" show-value label="重试次数" @update:model-value="(v: number) => { retry = v; markDirty(); }" />
      </SettingRow>
      <SettingRow label="各 Provider 是否走代理" desc="只读一览（去模型页修改 useProxy）">
        <div class="prov">
          <p v-for="p in providers" :key="p.id" class="prov-row">
            <span>{{ p.displayName }}</span>
            <span class="prov-sub">{{ p.id }}</span>
            <span class="tag">{{ p.useProxy ? (mode === 'direct' ? 'useProxy=true（全局 direct 下直连）' : '走代理') : '直连' }}</span>
          </p>
          <p v-if="providers.length === 0" class="prov-empty">还没有 Provider（先在“模型”页配置）。</p>
        </div>
      </SettingRow>
      <div class="page-actions">
        <HButton variant="primary" :disabled="!dirty" :loading="saving" @click="save">保存</HButton>
        <span v-if="dirty" class="dirty-hint" role="status">有未保存的改动</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.page { max-width: 760px; }
.page-title { font-size: var(--text-lg); font-weight: 600; margin: 0 0 var(--space-4); }
.page-state { display: flex; align-items: center; gap: var(--space-2); color: hsl(var(--fg-muted)); font-size: var(--text-sm); padding: var(--space-4) 0; }
.page-state.err { color: hsl(var(--danger)); flex-direction: column; align-items: flex-start; }
.prov { display: flex; flex-direction: column; gap: var(--space-1); }
.prov-row { display: flex; gap: var(--space-2); align-items: center; font-size: var(--text-sm); }
.prov-sub { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); }
.tag { font-size: var(--text-xs); background: hsl(var(--bg-muted)); padding: 0 var(--space-1); border-radius: var(--radius-sm); }
.prov-empty { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); }
 .prov-proto { display: flex; gap: var(--space-2); align-items: center; flex-wrap: wrap; }
 .prov-proto .hint { font-size: var(--text-xs); color: hsl(var(--fg-subtle)); }
.page-actions { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-3) 0; position: sticky; bottom: 0; background: hsl(var(--bg)); border-top: 1px solid hsl(var(--border-subtle)); }
.dirty-hint { font-size: var(--text-xs); color: hsl(var(--warning)); }
</style>
