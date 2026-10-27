<!-- src/Web.Client/src/components/settings/fields/ModelProbe.vue -->
<!-- 模型探测（01 §9.4）：loading + 计时（2s 后提示仍在探测）+ 超时 10s + 成功 Toast + 失败保留手填。 -->
<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import HButton from '@/components/base/HButton.vue';

const props = defineProps<{
  baseUrl: string;
  apiKey?: string;
}>();

const emit = defineEmits<{ 'probe-models': [models: string[]] }>();

const toast = useToast();
const probing = ref(false);
const elapsed = ref(0);
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
    const r = (await transport.request('settings.probeModels', {
      baseUrl: props.baseUrl,
      apiKey: props.apiKey || undefined,
    })) as { models?: string[] };
    if (timedOut) return;
    const models = Array.isArray(r.models) ? r.models : [];
    emit('probe-models', models);
    toast.success(`找到 ${models.length} 个模型`);
  } catch (e) {
    if (!timedOut) toast.error(`探测失败：${(e as Error).message}（可手动输入模型名）`);
  } finally {
    stopTimer();
    probing.value = false;
  }
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
