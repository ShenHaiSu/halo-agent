<!-- src/Web.Client/src/components/settings/pages/BehaviorPage.vue -->
<!-- P22 行为页：5 项全局行为（maxTurns/retry/persona/compactKeepTail/autoCompactOnLimit），一处可配持久生效。 -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { useSessionStore } from '@/stores/session';
import { openSettings } from '../SettingsApp.vue';
import SettingRow from '../fields/SettingRow.vue';
import HInput from '@/components/base/HInput.vue';
import HSlider from '@/components/base/HSlider.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HSelect from '@/components/base/HSelect.vue';
import HSwitch from '@/components/base/HSwitch.vue';
import { validateBehaviorForm } from '../validators';

const toast = useToast();
const session = useSessionStore();

const loading = ref(true);
const saving = ref(false);
const loadError = ref<string | null>(null);
const maxTurns = ref(200);
const retry = ref(0);
const persona = ref('');
const compactKeepTail = ref(2);
const autoCompactOnLimit = ref(false);
const personaOptions = ref<string[]>([]);
const saveWarnings = ref<string[]>([]);
const original = ref({ maxTurns: 200, retry: 0, persona: '', compactKeepTail: 2, autoCompactOnLimit: false });
const fieldErrors = ref<Record<string, string>>({});

const dirty = computed(
  () =>
    maxTurns.value !== original.value.maxTurns ||
    retry.value !== original.value.retry ||
    persona.value !== original.value.persona ||
    compactKeepTail.value !== original.value.compactKeepTail ||
    autoCompactOnLimit.value !== original.value.autoCompactOnLimit,
);

function markDirty(): void {
  fieldErrors.value = {};
}

function toInt(v: unknown, fallback: number): number {
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isInteger(n) ? n : fallback;
}

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = null;
  try {
    const s = (await transport.request('settings.get', null)) as {
      maxTurns?: number;
      retry?: number;
      persona?: string;
      compactKeepTail?: number;
      autoCompactOnLimit?: boolean;
    };
    let personas: string[] = [];
    try {
      const r = (await transport.request('asset.personas.list', null)) as { items?: unknown };
      if (Array.isArray(r.items)) personas = (r.items as unknown[]).filter((x): x is string => typeof x === 'string');
    } catch {
      personas = [];
    }
    maxTurns.value = typeof s.maxTurns === 'number' ? s.maxTurns : 200;
    retry.value = typeof s.retry === 'number' ? s.retry : 0;
    persona.value = typeof s.persona === 'string' ? s.persona : '';
    compactKeepTail.value = typeof s.compactKeepTail === 'number' ? s.compactKeepTail : 2;
    autoCompactOnLimit.value = s.autoCompactOnLimit === true;
    personaOptions.value = personas;
    // 下拉 comparator：Trim 后精确匹配，大小写敏感（后端同样口径）
    const cur = persona.value.trim();
    if (cur && !personaOptions.value.some((n) => n === cur)) {
      // 后端不存在会在保存时拒存；此处保留显示，不静默清空
    }
    original.value = {
      maxTurns: maxTurns.value,
      retry: retry.value,
      persona: persona.value,
      compactKeepTail: compactKeepTail.value,
      autoCompactOnLimit: autoCompactOnLimit.value,
    };
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
  const errs = validateBehaviorForm({
    maxTurns: maxTurns.value,
    retry: retry.value,
    compactKeepTail: compactKeepTail.value,
    persona: persona.value,
  });
  const map: Record<string, string> = {};
  for (const e of errs) map[e.field] = e.message;
  fieldErrors.value = map;
  if (errs.length > 0) {
    toast.warning('请先修正表单中的错误');
    return;
  }
  saving.value = true;
  try {
    const r = (await transport.request('settings.saveBehavior', {
      maxTurns: toInt(maxTurns.value, 200),
      retry: toInt(retry.value, 0),
      persona: persona.value.trim(),
      compactKeepTail: toInt(compactKeepTail.value, 2),
      autoCompactOnLimit: autoCompactOnLimit.value,
    })) as { warnings?: string[] };
    original.value = {
      maxTurns: maxTurns.value,
      retry: retry.value,
      persona: persona.value,
      compactKeepTail: compactKeepTail.value,
      autoCompactOnLimit: autoCompactOnLimit.value,
    };
    saveWarnings.value = Array.isArray(r.warnings) ? r.warnings : [];
    for (const w of saveWarnings.value) toast.warning(w);
    // 持久 vs 会话：刷新 session store 显示值，不经过 state.set* 二次写（避免会话级临时值掩盖全局值）
    try {
      session.maxTurns = maxTurns.value;
      session.retry = retry.value;
      session.persona = persona.value.trim();
    } catch {
      /* 忽略 */
    }
    toast.success('已保存');
    await load();
  } catch (e) {
    const err = e as { hint?: string; message?: string };
    if (err?.hint) {
      toast.error(`保存失败：${err.message ?? ''}（${err.hint}）`);
      // persona 拒存时按后端 hint 指引到资源页
      if ((err.hint as string).includes('资源')) openSettings('assets');
    } else {
      toast.error(`保存失败：${(e as Error).message}（输入已保留）`);
    }
  } finally {
    saving.value = false;
  }
}

function onMaxTurnsPreset(v: number): void {
  maxTurns.value = v;
  markDirty();
}

function isDirty(): boolean {
  return dirty.value;
}
function resetToSaved(): void {
  maxTurns.value = original.value.maxTurns;
  retry.value = original.value.retry;
  persona.value = original.value.persona;
  compactKeepTail.value = original.value.compactKeepTail;
  autoCompactOnLimit.value = original.value.autoCompactOnLimit;
  fieldErrors.value = {};
}
defineExpose({ isDirty, resetToSaved });
onMounted(() => void load());
</script>

<template>
  <div class="page">
    <h2 class="page-title">行为</h2>
    <div v-if="loading" class="page-state"><HSpinner size="sm" /><span>加载中…</span></div>
    <div v-else-if="loadError" class="page-state err" role="alert">
      <span>读取设置失败：{{ loadError }}</span>
      <HButton size="sm" variant="secondary" @click="load">重试</HButton>
    </div>
    <template v-else>
      <SettingRow label="单回合最大轮数" desc="0=不限，超限按第5项策略处理" :error="fieldErrors['maxTurns'] ?? null">
        <div class="col">
          <HInput
            :model-value="String(maxTurns)"
            type="number"
            :error="!!fieldErrors['maxTurns']"
            @update:model-value="(v: string) => { maxTurns = toInt(v, maxTurns); markDirty(); }"
          />
          <div class="presets">
            <HButton size="sm" variant="ghost" @click="onMaxTurnsPreset(50)">50</HButton>
            <HButton size="sm" variant="ghost" @click="onMaxTurnsPreset(200)">200</HButton>
            <HButton size="sm" variant="ghost" @click="onMaxTurnsPreset(500)">500</HButton>
            <HButton size="sm" variant="ghost" @click="onMaxTurnsPreset(0)">不限(0)</HButton>
          </div>
        </div>
      </SettingRow>
      <SettingRow label="弱网重试" desc="请求失败时最多重试次数" :error="fieldErrors['retry'] ?? null">
        <HSlider :model-value="retry" :min="0" :max="8" :step="1" show-value label="弱网重试" @update:model-value="(v: number) => { retry = v; markDirty(); }" />
      </SettingRow>
      <SettingRow label="默认人格" desc="新建会话时的人格；新建/编辑仍在资源页" :error="fieldErrors['persona'] ?? null">
        <HSelect :model-value="persona" :error="!!fieldErrors['persona']" @update:model-value="(v: string) => { persona = v; markDirty(); }">
          <option value="">无</option>
          <option v-for="n in personaOptions" :key="n" :value="n">{{ n }}</option>
        </HSelect>
      </SettingRow>
      <SettingRow label="压缩保留轮数" desc="压缩时保留最近 N 个完整回合，/compact 与自动压缩共用" :error="fieldErrors['compactKeepTail'] ?? null">
        <HInput
          :model-value="String(compactKeepTail)"
          type="number"
          :error="!!fieldErrors['compactKeepTail']"
          @update:model-value="(v: string) => { compactKeepTail = toInt(v, compactKeepTail); markDirty(); }"
        />
      </SettingRow>
      <SettingRow label="超限自动压缩" desc="达到上限时先自动压缩并继续，失败则弹窗询问；关=一律弹窗">
        <HSwitch
          :model-value="autoCompactOnLimit"
          label="超限自动压缩"
          @update:model-value="(v: boolean) => { autoCompactOnLimit = v; markDirty(); }"
        />
      </SettingRow>
      <div v-if="saveWarnings.length > 0" class="warn-box" role="status">
        <p v-for="(w, i) in saveWarnings" :key="i" class="warn-line">{{ w }}</p>
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
  max-width: var(--settings-page-max, 860px);
}
.page-title {
  font-size: var(--text-base);
  font-weight: 600;
  margin: 0 0 var(--space-2);
}
.page-state { display: flex; align-items: center; gap: var(--space-2); color: hsl(var(--fg-muted)); font-size: var(--text-sm); padding: var(--space-4) 0; }
.page-state.err { color: hsl(var(--danger)); flex-direction: column; align-items: flex-start; }
.col { display: flex; flex-direction: column; gap: var(--space-1); }
.presets { display: flex; gap: var(--space-1); flex-wrap: wrap; }
.warn-box { border: 1px solid hsl(var(--warning)); border-radius: var(--radius-sm); padding: var(--space-2); margin: var(--space-2) 0; }
.warn-line { font-size: var(--text-xs); color: hsl(var(--warning)); }
.page-actions { display: flex; align-items: center; gap: var(--space-2); padding: var(--space-2) 0; position: sticky; bottom: 0; background: hsl(var(--bg)); border-top: 1px solid hsl(var(--border-subtle)); }
.dirty-hint { font-size: var(--text-xs); color: hsl(var(--warning)); }
</style>
