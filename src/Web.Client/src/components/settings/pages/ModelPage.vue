<!-- src/Web.Client/src/components/settings/pages/ModelPage.vue -->
<!-- 模型分页：服务商 / 模型名可编辑下拉+探测 / 接口地址 / API Key / 上下文 HSlider / 最大轮数。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import SettingRow from '../fields/SettingRow.vue';
import SecretField from '../fields/SecretField.vue';
import ModelProbe from '../fields/ModelProbe.vue';
import HSelect from '@/components/base/HSelect.vue';
import HInput from '@/components/base/HInput.vue';
import HSlider from '@/components/base/HSlider.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
 import { validateModelForm } from '../validators';
 import {
   defaultModelOf,
   normalizeProviders,
   resolveProviderSwitch,
   type ProviderInfo,
 } from '../modelProviders';
 
 const toast = useToast();
 
 const loading = ref(true);
 const saving = ref(false);
const loadError = ref<string | null>(null);

const form = ref({ provider: '', model: '', baseUrl: '', contextWindow: 128000, maxTurns: 200 });
const original = ref({ ...form.value });
const apiKeySet = ref(false);
const newKey = ref('');
const probedModels = ref<string[]>([]);
const providers = ref<ProviderInfo[]>([]);
const fieldErrors = ref<Record<string, string>>({});

const dirty = computed(
  () =>
    form.value.provider !== original.value.provider ||
    form.value.model !== original.value.model ||
    form.value.baseUrl !== original.value.baseUrl ||
    form.value.contextWindow !== original.value.contextWindow ||
    form.value.maxTurns !== original.value.maxTurns ||
    newKey.value.length > 0
);

 const modelOptions = computed(() => {
   const p = providers.value.find((x) => x.name === form.value.provider);
   const base = p ? [defaultModelOf(p)] : [];
   const merged = new Set([...base.filter((m) => !!m), ...probedModels.value]);
   if (form.value.model.trim()) merged.add(form.value.model.trim());
   return [...merged];
 });

function markDirty(): void {
  fieldErrors.value = {};
}

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
     const s = (await transport.request('settings.get', null)) as {
       provider?: string;
       model?: string;
       baseUrl?: string;
       apiKeySet?: boolean;
       contextWindow?: number;
       maxTurns?: number;
       providers?: unknown;
     };
     providers.value = normalizeProviders(s.providers);
    form.value = {
      provider: s.provider ?? '',
      model: s.model ?? '',
      baseUrl: s.baseUrl ?? '',
      contextWindow: typeof s.contextWindow === 'number' ? s.contextWindow : 128000,
      maxTurns: typeof s.maxTurns === 'number' ? s.maxTurns : 200,
    };
    original.value = { ...form.value };
    apiKeySet.value = s.apiKeySet === true;
    newKey.value = '';
    fieldErrors.value = {};
   } catch (e) {
     loadError.value = (e as Error).message;
   } finally {
     loading.value = false;
   }
 }
 
 function onProviderChange(name: string): void {
   const prevName = form.value.provider;
   form.value.provider = name;
   const r = resolveProviderSwitch(providers.value, prevName, name, form.value.model);
   if (r) {
     form.value.baseUrl = r.baseUrl;
     // r.model 已按“跟随但尊重手填”算好（含保持原值的情况），直接写入
     form.value.model = r.model;
   }
   markDirty();
 }

function onProbe(models: string[]): void {
  probedModels.value = models;
  if (models[0] !== undefined && !form.value.model) {
    form.value.model = models[0];
    markDirty();
  }
}

function onMaxTurnsInput(v: string): void {
  const n = Math.trunc(Number(v));
  form.value.maxTurns = Number.isFinite(n) ? Math.min(1000, Math.max(1, n)) : 1;
  markDirty();
}

async function save(): Promise<void> {
  if (saving.value || loading.value) return;
  const errs = validateModelForm({ ...form.value, apiKey: newKey.value });
  const map: Record<string, string> = {};
  for (const e of errs) map[e.field] = e.message;
  fieldErrors.value = map;
  if (errs.length > 0) {
    toast.warning('请先修正表单中的错误');
    return;
  }
  saving.value = true;
  try {
    const r = (await transport.request('settings.saveModel', {
      provider: form.value.provider,
      model: form.value.model,
      baseUrl: form.value.baseUrl,
      apiKey: newKey.value || undefined,
      contextWindow: form.value.contextWindow,
      maxTurns: form.value.maxTurns,
    })) as { ok?: boolean; notes?: string[] };
    if (r.ok === false) {
      toast.error('保存失败（输入已保留）');
      return;
    }
    if (newKey.value) apiKeySet.value = true;
    newKey.value = ''; // * 保存成功立刻清空明文
    original.value = { ...form.value };
    fieldErrors.value = {};
    toast.success('已保存');
    for (const n of r.notes ?? []) toast.warning(n);
  } catch (e) {
    // * I-7：失败保留用户输入
    toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
  } finally {
    saving.value = false;
  }
}

async function clearKey(): Promise<void> {
  try {
    await transport.request('settings.saveModel', {
      provider: form.value.provider,
      model: form.value.model,
      baseUrl: form.value.baseUrl,
      clearKey: true,
      contextWindow: form.value.contextWindow,
      maxTurns: form.value.maxTurns,
    });
    apiKeySet.value = false;
    newKey.value = '';
    toast.success('已清除 API Key');
  } catch (e) {
    toast.error(`清除失败：${(e as Error).message}`);
  }
}

function isDirty(): boolean {
  return dirty.value;
}
function resetToSaved(): void {
  form.value = { ...original.value };
  newKey.value = '';
  fieldErrors.value = {};
}
defineExpose({ isDirty, resetToSaved });

onMounted(() => void load());
</script>

<template>
  <div class="page">
    <h2 class="page-title">模型</h2>

    <div v-if="loading" class="page-state"><HSpinner size="sm" /><span>加载中…</span></div>
    <div v-else-if="loadError" class="page-state err" role="alert">
      <span>读取设置失败：{{ loadError }}</span>
      <HButton size="sm" variant="secondary" @click="load">重试</HButton>
    </div>

    <template v-else>
      <SettingRow
        label="服务商"
        desc="模型服务商（provider）"
        required
        :error="fieldErrors['provider'] ?? null"
      >
        <HSelect
          :model-value="form.provider"
          placeholder="请选择"
          @update:model-value="onProviderChange"
        >
          <option v-for="p in providers" :key="p.name" :value="p.name">{{ p.name }}</option>
        </HSelect>
      </SettingRow>

      <SettingRow
        label="模型"
        desc="模型名（model）。可从列表选择，也可手动输入"
        required
        :error="fieldErrors['model'] ?? null"
      >
        <div class="model-row">
          <HSelect
            :model-value="form.model"
            placeholder="请选择或手填"
            @update:model-value="
              (v: string) => {
                form.model = v;
                markDirty();
              }
            "
          >
            <option v-for="m in modelOptions" :key="m" :value="m">{{ m }}</option>
          </HSelect>
          <HInput
            :model-value="form.model"
            placeholder="或手动输入模型名"
            @update:model-value="
              (v: string) => {
                form.model = v;
                markDirty();
              }
            "
          />
          <ModelProbe :base-url="form.baseUrl" :api-key="newKey" @probe-models="onProbe" />
        </div>
      </SettingRow>

      <SettingRow
        label="接口地址"
        desc="接口地址（baseUrl），形如 https://api.example.com/v1"
        required
        :error="fieldErrors['baseUrl'] ?? null"
      >
        <HInput
          :model-value="form.baseUrl"
          placeholder="https://"
          :error="!!fieldErrors['baseUrl']"
          @update:model-value="
            (v: string) => {
              form.baseUrl = v;
              markDirty();
            }
          "
        />
      </SettingRow>

      <SettingRow
        label="API Key"
        desc="保存后不再回显明文；留空表示不修改"
        :error="fieldErrors['apiKey'] ?? null"
      >
        <SecretField v-model="newKey" :api-key-set="apiKeySet" @update:model-value="markDirty" />
        <HButton v-if="apiKeySet" size="sm" variant="ghost" @click="clearKey">清除 API Key</HButton>
      </SettingRow>

      <SettingRow
        label="上下文长度"
        desc="模型能“记住”的最大 token 数（contextWindow），8000 ~ 1000000"
        :error="fieldErrors['contextWindow'] ?? null"
      >
        <HSlider
          :model-value="form.contextWindow"
          :min="8000"
          :max="1000000"
          :step="8000"
          show-value
          label="上下文长度"
          @update:model-value="
            (v: number) => {
              form.contextWindow = v;
              markDirty();
            }
          "
        />
      </SettingRow>

      <SettingRow
        label="最大轮数"
        desc="一个回合内最多执行多少轮工具调用（maxTurns），1 ~ 1000"
        :error="fieldErrors['maxTurns'] ?? null"
      >
        <HInput
          :model-value="String(form.maxTurns)"
          type="number"
          :error="!!fieldErrors['maxTurns']"
          @update:model-value="onMaxTurnsInput"
        />
      </SettingRow>

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
.model-row {
  display: flex;
  gap: var(--space-2);
  align-items: center;
  flex-wrap: wrap;
  min-width: 0;
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
