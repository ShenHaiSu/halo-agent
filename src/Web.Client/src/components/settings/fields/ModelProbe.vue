<!-- src/Web.Client/src/components/settings/fields/ModelProbe.vue -->
<!-- 模型探测：loading + 计时 + 超时 10s + 成功弹名单 dialog + 失败保留手填。P10：带头、走对代理；含会话模板的头跳过。P12：名单闭环 + 空 headers 省传。 -->
<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import HButton from '@/components/base/HButton.vue';
import ProbeResultDialog from './ProbeResultDialog.vue';
import type { HeaderRow } from './HeadersEditor.vue';

const props = defineProps<{
  baseUrl: string;
  apiKey?: string;
  providerId?: string;
  headers?: HeaderRow[];
  useProxy?: boolean;
  /** 用户已点“清除”但未保存：探测不回落旧 key。 */
  clearKey?: boolean;
  /** 已在列表的模型 id（供 dialog 标记已存在）。 */
  existingModels?: string[];
}>();

const emit = defineEmits<{ 'probe-models': [models: string[]]; 'probe-confirm': [models: string[]] }>();

const toast = useToast();
const probing = ref(false);
const elapsed = ref(0);
const showDialog = ref(false);
const foundModels = ref<string[]>([]);
let timer: number | null = null;
let timedOut = false;

function stopTimer(): void {
  if (timer !== null) {
    window.clearInterval(timer);
    timer = null;
  }
}

async function probe(): Promise<void> {
  if (probing.value) return;
  probing.value = true;
  timedOut = false;
  elapsed.value = 0;
  const t0 = performance.now();
  timer = window.setInterval(() => {
    elapsed.value = Math.round((performance.now() - t0) / 100) / 10;
    if (elapsed.value >= 10) {
      stopTimer();
      probing.value = false;
      timedOut = true;
      toast.warning('探测超时，可手动输入模型名');
    }
  }, 100);

  try {
    // 空 headers 省传：后端把“缺字段”视同回落库中头，空数组不再占位。
    const headers = props.headers?.filter((h) => h.key.trim() !== '' || h.value !== '') ?? [];
    const params: Record<string, unknown> = {
      baseUrl: props.baseUrl,
      apiKey: props.apiKey || undefined,
      providerId: props.providerId,
      useProxy: props.useProxy,
    };
    if (headers.length > 0) params['headers'] = headers;
    if (props.clearKey === true) params['clearKey'] = true;
    const r = (await transport.request('settings.probeModels', params)) as { models?: string[] };
    if (timedOut) return;
    const models = Array.isArray(r.models) ? r.models : [];
    foundModels.value = models;
    emit('probe-models', models);
    // 成功即弹名单 dialog（含 0 个空态），不只 toast。
    showDialog.value = true;
  } catch (e) {
    if (!timedOut) toast.error(`探测失败：${(e as Error).message}（可手动输入模型名）`);
  } finally {
    stopTimer();
    probing.value = false;
  }
}

function onDialogConfirm(names: string[]): void {
  emit('probe-confirm', names);
}

onBeforeUnmount(() => stopTimer());
</script>

<template>
  <div class="probe">
    <HButton size="sm" variant="secondary" :loading="probing" :disabled="!baseUrl" @click="probe">
      {{ probing ? `探测中…（${elapsed.toFixed(1)}s）` : '探测可用模型' }}
    </HButton>
    <span v-if="probing && elapsed >= 2" class="probe-hint" role="status"
      >仍在探测（共约 10 秒），请稍候…</span
    >
    <ProbeResultDialog
      :open="showDialog"
      :models="foundModels"
      :existing="existingModels ?? []"
      @update:open="(v: boolean) => (showDialog = v)"
      @close="showDialog = false"
      @confirm="onDialogConfirm"
    />
  </div>
</template>

<style scoped>
.probe {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}
.probe-hint {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
</style>
