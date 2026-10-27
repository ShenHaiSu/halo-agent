<!-- src/Web.Client/src/components/settings/pages/NetworkPage.vue -->
<!-- 网络分页：代理模式三选一 / 代理地址 / 重试 0~10 / 自定义请求头 / 分服务商请求头。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import HeadersEditor, { type HeaderRow } from '../fields/HeadersEditor.vue';
import HInput from '@/components/base/HInput.vue';
import HSlider from '@/components/base/HSlider.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HSelect from '@/components/base/HSelect.vue';
import HRadioGroup from '@/components/base/HRadioGroup.vue';
import HRadio from '@/components/base/HRadio.vue';
import { validateNetworkForm } from '../validators';

const toast = useToast();

type ProxyMode = 'direct' | 'system' | 'custom';

interface NetworkSnapshot {
  mode: ProxyMode;
  url: string;
  retry: number;
  headers: HeaderRow[];
  providerHeaders: Record<string, HeaderRow[]>;
}

const loading = ref(true);
const saving = ref(false);
const loadError = ref<string | null>(null);

const mode = ref<ProxyMode>('direct');
const url = ref('');
const retry = ref(3);
const headers = ref<HeaderRow[]>([]);
const providerHeaders = ref<Record<string, HeaderRow[]>>({});
const providerNames = ref<string[]>([]);
const activeProvider = ref('');
const notes = ref<string[]>([]);
const fieldErrors = ref<Record<string, string>>({});

const original = ref<NetworkSnapshot>({
  mode: 'direct',
  url: '',
  retry: 3,
  headers: [],
  providerHeaders: {},
});

function snapshot(): NetworkSnapshot {
  return {
    mode: mode.value,
    url: url.value,
    retry: retry.value,
    headers: headers.value.map((h) => ({ ...h })),
    providerHeaders: Object.fromEntries(
      Object.entries(providerHeaders.value).map(([k, v]) => [k, v.map((h) => ({ ...h }))])
    ),
  };
}

const dirty = computed(() => JSON.stringify(snapshot()) !== JSON.stringify(original.value));

const activeProviderHeaders = computed<HeaderRow[]>({
  get: () => (activeProvider.value ? (providerHeaders.value[activeProvider.value] ?? []) : []),
  set: (v) => {
    if (activeProvider.value)
      providerHeaders.value = { ...providerHeaders.value, [activeProvider.value]: v };
  },
});

function markDirty(): void {
  fieldErrors.value = {};
}

function normalizeMode(m: unknown): ProxyMode {
  return m === 'system' || m === 'custom' ? m : 'direct';
}

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  notes.value = [];
  try {
    const s = (await transport.request('settings.get', null)) as {
      network?: {
        mode?: string;
        url?: string;
        retry?: number;
        headers?: HeaderRow[];
        providerHeaders?: Record<string, HeaderRow[]>;
      };
      providers?: Array<{ name?: string }>;
    };
    const n = s.network ?? {};
    mode.value = normalizeMode(n.mode);
    url.value = typeof n.url === 'string' ? n.url : '';
    retry.value = typeof n.retry === 'number' ? n.retry : 3;
    headers.value = Array.isArray(n.headers) ? n.headers : [];
    providerHeaders.value =
      n.providerHeaders && typeof n.providerHeaders === 'object' ? n.providerHeaders : {};
    providerNames.value = (s.providers ?? []).map((p) => p?.name ?? '').filter((x) => x !== '');
    const keys = Object.keys(providerHeaders.value);
    activeProvider.value = providerNames.value[0] ?? keys[0] ?? '';
    original.value = snapshot();
    fieldErrors.value = {};
  } catch (e) {
    loadError.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}

async function save(): Promise<void> {
  if (saving.value || loading.value) return;
  const errs = validateNetworkForm({
    mode: mode.value,
    url: url.value,
    retry: retry.value,
    headers: headers.value,
  });
  const map: Record<string, string> = {};
  for (const e of errs) map[e.field] = e.message;
  // 分服务商头的 key 校验
  for (const [p, list] of Object.entries(providerHeaders.value)) {
    list.forEach((h, i) => {
      if (h.key.trim() && /[\s:]/.test(h.key)) {
        map[`providerHeaders.${p}[${i}]`] = `服务商「${p}」第 ${i + 1} 行请求头名称不合法`;
      }
    });
  }
  fieldErrors.value = map;
  if (Object.keys(map).length > 0) {
    toast.warning('请先修正表单中的错误');
    return;
  }
  saving.value = true;
  try {
    const r = (await transport.request('settings.saveNetwork', {
      mode: mode.value,
      url: mode.value === 'custom' ? url.value : undefined,
      retry: retry.value,
      headers: headers.value.filter((h) => h.key.trim() !== ''),
      providerHeaders: providerHeaders.value,
    })) as { ok?: boolean; notes?: string[] };
    if (r.ok === false) {
      toast.error('保存失败（输入已保留）');
      return;
    }
    notes.value = r.notes ?? [];
    original.value = snapshot();
    toast.success('已保存');
  } catch (e) {
    toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
  } finally {
    saving.value = false;
  }
}

function isDirty(): boolean {
  return dirty.value;
}
function resetToSaved(): void {
  const o = original.value;
  mode.value = o.mode;
  url.value = o.url;
  retry.value = o.retry;
  headers.value = o.headers.map((h) => ({ ...h }));
  providerHeaders.value = Object.fromEntries(
    Object.entries(o.providerHeaders).map(([k, v]) => [k, v.map((h) => ({ ...h }))])
  );
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
      <SettingRow label="代理模式" desc="直连 / 使用系统代理 / 自定义代理地址">
        <HRadioGroup
          :model-value="mode"
          name="proxy-mode"
          direction="horizontal"
          @update:model-value="
            (v: string | number) => {
              mode = v === 'system' ? 'system' : v === 'custom' ? 'custom' : 'direct';
              markDirty();
            }
          "
        >
          <HRadio value="direct" label="直连" />
          <HRadio value="system" label="系统代理" />
          <HRadio value="custom" label="自定义" />
        </HRadioGroup>
      </SettingRow>

      <SettingRow
        v-if="mode === 'custom'"
        label="代理地址"
        desc="仅自定义模式需要，http(s):// 开头"
        required
        :error="fieldErrors['url'] ?? null"
      >
        <HInput
          :model-value="url"
          placeholder="http://127.0.0.1:7890"
          :error="!!fieldErrors['url']"
          @update:model-value="
            (v: string) => {
              url = v;
              markDirty();
            }
          "
        />
      </SettingRow>

      <SettingRow
        label="重试次数"
        desc="请求失败时最多重试次数，0 ~ 10"
        :error="fieldErrors['retry'] ?? null"
      >
        <HSlider
          :model-value="retry"
          :min="0"
          :max="10"
          :step="1"
          show-value
          label="重试次数"
          @update:model-value="
            (v: number) => {
              retry = v;
              markDirty();
            }
          "
        />
      </SettingRow>

      <SettingRow label="自定义请求头" desc="附加到所有模型请求的请求头（headers）">
        <HeadersEditor
          :model-value="headers"
          @update:model-value="
            (v: HeaderRow[]) => {
              headers = v;
              markDirty();
            }
          "
        />
      </SettingRow>

      <SettingRow label="分服务商请求头" desc="只附加到指定服务商请求的请求头（providerHeaders）">
        <div class="prov">
          <HSelect
            :model-value="activeProvider"
            placeholder="选择服务商"
            @update:model-value="
              (v: string) => {
                activeProvider = v;
              }
            "
          >
            <option v-for="p in providerNames" :key="p" :value="p">{{ p }}</option>
          </HSelect>
          <HeadersEditor
            v-if="activeProvider"
            :model-value="activeProviderHeaders"
            @update:model-value="
              (v: HeaderRow[]) => {
                activeProviderHeaders = v;
                markDirty();
              }
            "
          />
          <p v-else class="prov-empty">还没有可用的服务商（先在“模型”页配置服务商）。</p>
        </div>
      </SettingRow>

      <div v-if="notes.length" class="notes" role="status">
        <p v-for="(n, i) in notes" :key="i" class="note">{{ n }}</p>
      </div>

      <div class="page-actions">
        <HButton variant="primary" :disabled="!dirty" :loading="saving" @click="save">保存</HButton>
        <span v-if="dirty" class="dirty-hint" role="status">有未保存的改动</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.page {
  max-width: 760px;
}
.page-title {
  font-size: var(--text-lg);
  font-weight: 600;
  margin: 0 0 var(--space-4);
}
.page-state {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: hsl(var(--fg-muted));
  font-size: var(--text-sm);
  padding: var(--space-4) 0;
}
.page-state.err {
  color: hsl(var(--danger));
  flex-direction: column;
  align-items: flex-start;
}
.prov {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  min-width: 0;
}
.prov-empty {
  margin: 0;
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
}
.notes {
  margin-top: var(--space-2);
}
.note {
  margin: 0 0 var(--space-1);
  font-size: var(--text-xs);
  padding: var(--space-1) var(--space-2);
  border-radius: var(--radius-sm);
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
}
.page-actions {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: var(--space-3) 0;
  position: sticky;
  bottom: 0;
  background: hsl(var(--bg));
  border-top: 1px solid hsl(var(--border-subtle));
}
.dirty-hint {
  font-size: var(--text-xs);
  color: hsl(var(--warning));
}
</style>
