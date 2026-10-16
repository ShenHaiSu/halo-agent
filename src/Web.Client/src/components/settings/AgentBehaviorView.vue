<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden" data-testid="agent-behavior-tab">
    <div v-if="agentStore.loading" class="flex justify-center py-8">
      <n-spin size="medium" />
    </div>
    <div v-else-if="!agentStore.config" class="text-center py-8">
      <p class="text-sm text-secondary mb-3">配置加载失败</p>
      <n-button size="small" @click="onRetry">重试</n-button>
    </div>
    <template v-else>
      <!-- A. 说明与保存区 -->
      <n-card title="说明与保存" size="small">
        <n-alert
          v-if="agentStore.config?.fallbackReason"
          type="warning"
          :show-icon="true"
          class="mb-3"
        >
          {{ agentStore.config?.fallbackReason }}
        </n-alert>
        <p class="text-xs text-secondary">
          生效范围：全局 <code>config/agent.json</code>，保存后下一轮对话起生效；执行中/流式中的本轮不受影响。超时单位为毫秒，0 表示无限。
        </p>
        <div class="flex items-center gap-2 mt-3 flex-wrap">
          <n-button
            type="primary"
            size="small"
            data-testid="agent-save"
            :loading="agentStore.saving"
            :disabled="!agentStore.dirty || agentStore.saving || dmInvalid || overLimit"
            @click="onSave"
          >
            保存
          </n-button>
          <n-button
            size="small"
            type="error"
            ghost
            data-testid="agent-reset-all"
            @click="onResetAll"
          >
            恢复全部默认
          </n-button>
          <span v-if="agentStore.dirty" class="text-xs text-amber-500">尚未保存，离开将丢失</span>
        </div>
      </n-card>

      <!-- B. 命令执行超时区 -->
      <n-card title="命令执行超时" size="small">
        <!-- 默认超时 -->
        <div ref="rowDefaultTimeoutRef" class="space-y-1 min-w-0">
          <div class="flex items-center gap-2 flex-wrap">
            <label class="text-[13px] font-medium min-w-[96px]">默认超时</label>
            <n-input-number
              :value="agentStore.config.execDefaultTimeoutMs"
              :min="0"
              :max="3600000"
              :step="5000"
              :show-button="false"
              size="small"
              class="font-mono text-xs"
              style="width: 180px;"
              data-testid="agent-timeout-default-input"
              @update:value="onDefaultTimeoutUpdate"
            >
              <template #suffix>ms</template>
            </n-input-number>
            <span class="text-xs font-mono" :class="agentStore.config.execDefaultTimeoutMs === 0 ? 'text-red-500' : 'text-secondary'">
              {{ fmtMs(agentStore.config.execDefaultTimeoutMs) }}
            </span>
            <n-popconfirm @positive-click="() => onResetField('defaultTimeout', '默认超时 30s')">
              <template #trigger>
                <n-button size="tiny" data-testid="agent-reset-defaultTimeout">恢复本项默认</n-button>
              </template>
              确认恢复本项为默认？默认超时 30s
            </n-popconfirm>
          </div>
          <div class="flex flex-wrap items-center gap-1.5 pl-[104px]">
            <n-button
              v-for="p in DEFAULT_TIMEOUT_PRESETS"
              :key="p.value"
              size="tiny"
              secondary
              :type="agentStore.config.execDefaultTimeoutMs === p.value ? 'primary' : 'default'"
              :class="{ 'preset-danger': p.value === 0 && agentStore.config.execDefaultTimeoutMs === 0 }"
              :data-testid="`preset-timeout-default-${p.testid}`"
              @click="() => onDefaultTimeoutUpdate(p.value)"
            >{{ p.label }}</n-button>
          </div>
          <p v-if="fieldError('execDefaultTimeoutMs')" class="text-xs text-red-500 pl-[104px]">
            {{ fieldError('execDefaultTimeoutMs') }}
          </p>
        </div>

        <n-divider style="margin: 12px 0;" />

        <!-- 上限 -->
        <div ref="rowMaxTimeoutRef" class="space-y-1 min-w-0">
          <div class="flex items-center gap-2 flex-wrap">
            <label class="text-[13px] font-medium min-w-[96px]">上限</label>
            <n-input-number
              :value="agentStore.config.execMaxTimeoutMs"
              :min="0"
              :max="3600000"
              :step="5000"
              :show-button="false"
              size="small"
              class="font-mono text-xs"
              style="width: 180px;"
              data-testid="agent-timeout-max-input"
              @update:value="onMaxTimeoutUpdate"
            >
              <template #suffix>ms</template>
            </n-input-number>
            <span class="text-xs font-mono" :class="agentStore.config.execMaxTimeoutMs === 0 ? 'text-red-500' : 'text-secondary'">
              {{ fmtMax(agentStore.config.execMaxTimeoutMs) }}
            </span>
            <n-popconfirm @positive-click="() => onResetField('maxTimeout', '上限 300s')">
              <template #trigger>
                <n-button size="tiny" data-testid="agent-reset-maxTimeout">恢复本项默认</n-button>
              </template>
              确认恢复本项为默认？上限 300s
            </n-popconfirm>
          </div>
          <div class="flex flex-wrap items-center gap-1.5 pl-[104px]">
            <n-button
              v-for="p in MAX_TIMEOUT_PRESETS"
              :key="p.value"
              size="tiny"
              secondary
              :type="agentStore.config.execMaxTimeoutMs === p.value ? 'primary' : 'default'"
              :class="{ 'preset-danger': p.value === 0 && agentStore.config.execMaxTimeoutMs === 0 }"
              :data-testid="`preset-timeout-max-${p.testid}`"
              @click="() => onMaxTimeoutUpdate(p.value)"
            >{{ p.label }}</n-button>
          </div>
          <p v-if="fieldError('execMaxTimeoutMs')" class="text-xs text-red-500 pl-[104px]">
            {{ fieldError('execMaxTimeoutMs') }}
          </p>
        </div>

        <n-alert
          v-if="dmInvalid"
          type="warning"
          :show-icon="false"
          class="mt-3"
        >
          默认超时不得大于上限，请先调大上限或调小默认
        </n-alert>
        <n-alert
          v-if="hasInfiniteTimeout"
          type="error"
          :show-icon="true"
          class="mt-3"
        >
          你设置了无限等待：命令永不超时，仅手动取消可终止。请确认长任务真的需要，并保持窗口开启。
        </n-alert>
      </n-card>

      <!-- C. 工具轮次区 -->
      <n-card title="工具轮次" size="small">
        <div ref="rowIterationsRef" class="space-y-1 min-w-0">
          <div class="flex items-center gap-2 flex-wrap">
            <label class="text-[13px] font-medium min-w-[96px]">最大轮次</label>
            <n-input-number
              :value="agentStore.config.maxIterations"
              :min="0"
              :max="500"
              :step="1"
              size="small"
              class="font-mono text-xs"
              style="width: 180px;"
              data-testid="agent-iterations-input"
              @update:value="onIterationsUpdate"
            />
            <span class="text-xs font-mono" :class="agentStore.config.maxIterations === 0 ? 'text-red-500' : 'text-secondary'">
              {{ fmtIter(agentStore.config.maxIterations) }}
            </span>
            <n-popconfirm @positive-click="() => onResetField('iterations', '轮次 25')">
              <template #trigger>
                <n-button size="tiny" data-testid="agent-reset-iterations">恢复本项默认</n-button>
              </template>
              确认恢复本项为默认？轮次 25
            </n-popconfirm>
          </div>
          <div class="flex flex-wrap items-center gap-1.5 pl-[104px]">
            <n-button
              v-for="p in ITERATIONS_PRESETS"
              :key="p.value"
              size="tiny"
              secondary
              :type="agentStore.config.maxIterations === p.value ? 'primary' : 'default'"
              :class="{ 'preset-danger': p.value === 0 && agentStore.config.maxIterations === 0 }"
              :data-testid="`preset-iterations-${p.testid}`"
              @click="() => onIterationsUpdate(p.value)"
            >{{ p.label }}</n-button>
          </div>
          <p v-if="fieldError('maxIterations')" class="text-xs text-red-500 pl-[104px]">
            {{ fieldError('maxIterations') }}
          </p>
        </div>
        <n-alert
          v-if="agentStore.config.maxIterations === 0"
          type="error"
          :show-icon="true"
          class="mt-3"
        >
          你设置了无限轮次：永不截断，仅模型停手/手动取消可结束，Token 消耗无上限。请谨慎开启。
        </n-alert>
        <p class="text-xs text-secondary mt-2">
          刹车说明：无限轮次下唯一的刹车是用户取消、模型自己停手（不再发 ToolCall）与 Token 预算裁剪。
        </p>
      </n-card>

      <!-- FUTURE: 杂项卡片槽：自动继续/工具白名单/压缩阈值等在此追加 n-card，不新增 Tab -->
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { useMessage, useDialog } from 'naive-ui';
import { useAgentConfigStore } from '@/stores/agentConfigStore';

const message = useMessage();
const dialog = useDialog();
const agentStore = useAgentConfigStore();

const rowDefaultTimeoutRef = ref<HTMLElement | null>(null);
const rowMaxTimeoutRef = ref<HTMLElement | null>(null);
const rowIterationsRef = ref<HTMLElement | null>(null);

const DEFAULT_TIMEOUT_PRESETS = [
  { label: '30s', value: 30000, testid: '30s' },
  { label: '60s', value: 60000, testid: '60s' },
  { label: '120s', value: 120000, testid: '120s' },
  { label: '300s', value: 300000, testid: '300s' },
  { label: '∞', value: 0, testid: 'inf' },
];
const MAX_TIMEOUT_PRESETS = [
  { label: '60s', value: 60000, testid: '60s' },
  { label: '300s', value: 300000, testid: '300s' },
  { label: '1h', value: 3600000, testid: '1h' },
  { label: '不限', value: 0, testid: 'inf' },
];
const ITERATIONS_PRESETS = [
  { label: '10', value: 10, testid: '10' },
  { label: '25', value: 25, testid: '25' },
  { label: '50', value: 50, testid: '50' },
  { label: '100', value: 100, testid: '100' },
  { label: '∞', value: 0, testid: 'inf' },
];

const dmInvalid = computed(() => {
  const c = agentStore.config;
  if (!c) return false;
  return c.execDefaultTimeoutMs > 0 && c.execMaxTimeoutMs > 0 && c.execDefaultTimeoutMs > c.execMaxTimeoutMs;
});

const overLimit = computed(() => {
  const c = agentStore.config;
  if (!c) return false;
  return c.execDefaultTimeoutMs > 3600000 || c.execMaxTimeoutMs > 3600000 || c.maxIterations > 500;
});

const hasInfiniteTimeout = computed(() => {
  const c = agentStore.config;
  if (!c) return false;
  return c.execDefaultTimeoutMs === 0 || c.execMaxTimeoutMs === 0;
});

function fmtMs(v: number): string {
  if (v === 0) return '=无限';
  if (v < 1000) return `=${v}ms`;
  return `=${v / 1000}s`;
}
function fmtMax(v: number): string {
  if (v === 0) return '=不限上限';
  if (v < 1000) return `=${v}ms`;
  if (v % 60000 === 0) return `=${v / 60000}m`;
  return `=${v / 1000}s`;
}
function fmtIter(v: number): string {
  if (v === 0) return '=无限';
  return `=${v}轮`;
}

function fieldError(field: string): string | null {
  const e = agentStore.lastError;
  if (!e) return null;
  if (e.field === field) return e.message;
  return null;
}

function scrollToField(field: string) {
  const map: Record<string, typeof rowDefaultTimeoutRef> = {
    execDefaultTimeoutMs: rowDefaultTimeoutRef,
    execMaxTimeoutMs: rowMaxTimeoutRef,
    maxIterations: rowIterationsRef,
  };
  const target = map[field]?.value;
  if (target && typeof (target as HTMLElement).scrollIntoView === 'function') {
    (target as HTMLElement).scrollIntoView({ behavior: 'smooth', block: 'center' });
  }
}

function applyNumber(field: 'execDefaultTimeoutMs' | 'execMaxTimeoutMs' | 'maxIterations', next: number | null) {
  const c = agentStore.config;
  if (!c) return;
  const v = next ?? 0;
  if (c[field] === v) return;
  c[field] = v;
  agentStore.touchDirty();
}

function confirmInfiniteWait(onOk: () => void) {
  dialog.warning({
    title: '确认为无限？',
    content: '确认为无限等待？命令将永不超时，仅手动取消可终止，误配可能导致进程挂起。请确认长任务真的需要。',
    positiveText: '确认为无限',
    negativeText: '取消',
    onPositiveClick: () => { onOk(); },
  });
}

function confirmInfiniteIterations(onOk: () => void) {
  dialog.warning({
    title: '确认为无限？',
    content: '确认为无限轮次？循环将永不截断，仅模型停手或手动取消可结束，Token 消耗无上限。请谨慎开启。',
    positiveText: '确认为无限',
    negativeText: '取消',
    onPositiveClick: () => { onOk(); },
  });
}

function onDefaultTimeoutUpdate(v: number | null) {
  const c = agentStore.config;
  if (!c) return;
  const next = v ?? 0;
  if (c.execDefaultTimeoutMs > 0 && next === 0) {
    confirmInfiniteWait(() => applyNumber('execDefaultTimeoutMs', 0));
    return;
  }
  applyNumber('execDefaultTimeoutMs', next);
}

function onMaxTimeoutUpdate(v: number | null) {
  const c = agentStore.config;
  if (!c) return;
  const next = v ?? 0;
  if (c.execMaxTimeoutMs > 0 && next === 0) {
    confirmInfiniteWait(() => applyNumber('execMaxTimeoutMs', 0));
    return;
  }
  applyNumber('execMaxTimeoutMs', next);
}

function onIterationsUpdate(v: number | null) {
  const c = agentStore.config;
  if (!c) return;
  const next = v ?? 0;
  if (c.maxIterations > 0 && next === 0) {
    confirmInfiniteIterations(() => applyNumber('maxIterations', 0));
    return;
  }
  applyNumber('maxIterations', next);
}

async function onSave() {
  const res = await agentStore.saveConfig();
  if (res.ok) {
    message.success('智能体行为配置已保存，下一轮对话起生效');
  } else {
    message.error(res.message || '保存失败');
    if (agentStore.lastError) scrollToField(agentStore.lastError.field);
  }
}

function onResetAll() {
  dialog.error({
    title: '恢复全部默认',
    content: '确认将智能体行为 3 项全部恢复为默认（默认超时 30s / 上限 300s / 轮次 25）？当前编辑将丢失。',
    positiveText: '确认恢复',
    negativeText: '取消',
    onPositiveClick: async () => {
      const res = await agentStore.resetAll();
      if (res.ok) {
        message.success('已恢复全部默认');
      } else {
        message.error(res.message || '恢复失败');
      }
    },
  });
}

async function onResetField(field: 'defaultTimeout' | 'maxTimeout' | 'iterations', _label: string) {
  void _label;
  const res = await agentStore.resetField(field);
  if (res.ok) {
    message.success('已恢复本项默认');
  } else {
    message.error(res.message || '恢复失败');
  }
}

async function onRetry() {
  await agentStore.loadConfig();
}

onMounted(async () => {
  agentStore.watchStorageChange();
  if (!agentStore.config) await agentStore.loadConfig();
});
</script>

<style scoped>
.preset-danger {
  border-color: #d03050 !important;
}
</style>
