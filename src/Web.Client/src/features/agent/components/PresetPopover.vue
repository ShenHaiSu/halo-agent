<template>
  <n-popover
    trigger="click"
    placement="top-start"
    :show-arrow="false"
    style="width: 280px"
    data-testid="composer-preset-pop"
    @update:show="onShowChange"
  >
    <template #trigger>
      <n-button secondary size="small" data-testid="composer-preset" :loading="loading">
        <span class="preset-dot" :style="{ background: dot }"></span>
        Preset · {{ label }}
      </n-button>
    </template>
    <div class="space-y-2.5 text-xs" data-testid="preset-panel">
      <div class="text-secondary">保存后下一轮生效，本轮不变。</div>
      <n-radio-group v-model:value="draftPreset" size="small" data-testid="preset-radio">
        <n-space vertical>
          <n-radio value="coding">Coding · 编码默认</n-radio>
          <n-radio value="readonly">ReadOnly · 只读</n-radio>
          <n-radio value="full">Full · 全部</n-radio>
        </n-space>
      </n-radio-group>
      <n-alert v-if="err" type="error" :show-icon="false" class="text-xs">{{ err }}</n-alert>
      <div class="flex items-center justify-end gap-2 pt-1">
        <n-button size="tiny" ghost :disabled="loading" data-testid="preset-reset" @click="onReset">恢复默认</n-button>
        <n-button size="tiny" type="primary" :loading="loading" data-testid="preset-save" @click="onSave">保存·下一轮生效</n-button>
      </div>
    </div>
  </n-popover>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { useMessage } from 'naive-ui';
import { useAgentConfigStore } from '@/stores/agentConfigStore';
import { presetLabel, dotColor, normalizePreset } from '@shared/utils/toolPreset';

const message = useMessage();
const agentStore = useAgentConfigStore();
const loading = ref(false);
const err = ref('');
const draftPreset = ref('coding');

const label = computed(() => presetLabel(agentStore.config?.toolsPreset));
const dot = computed(() => dotColor(agentStore.config?.toolsPreset));

function syncFromStore(): void {
  draftPreset.value = normalizePreset(agentStore.config?.toolsPreset);
  err.value = '';
}

async function ensureLoaded(): Promise<void> {
  if (!agentStore.config) await agentStore.loadConfig();
  syncFromStore();
}

function onShowChange(show: boolean): void {
  if (show) void ensureLoaded();
}

async function onSave(): Promise<void> {
  // 三态切换：只写 toolsPreset，并清空 allowlist/denylist，不再点名单个 tool。
  if (!agentStore.config) await agentStore.loadConfig();
  const c = agentStore.config;
  if (!c) return;
  loading.value = true;
  err.value = '';
  try {
    const rec = c as unknown as Record<string, unknown>;
    if (draftPreset.value === 'coding') rec.toolsPreset = null;
    else rec.toolsPreset = draftPreset.value;
    rec.toolsAllowlist = null;
    rec.toolsDenylist = null;
    agentStore.touchDirty();
    const res = await agentStore.saveConfig();
    if ((res as unknown as { ok: boolean }).ok) message.success('Preset 已保存，下一轮生效');
    else {
      err.value = (res as unknown as { message?: string }).message || '保存失败';
      message.error(err.value);
    }
  } finally {
    loading.value = false;
  }
}

async function onReset(): Promise<void> {
  loading.value = true;
  try {
    const res = await agentStore.resetField('toolspreset');
    if ((res as unknown as { ok: boolean }).ok) {
      message.success('已恢复默认 Coding');
      syncFromStore();
    } else message.error((res as unknown as { message?: string }).message || '恢复失败');
  } finally {
    loading.value = false;
  }
}
</script>

<style scoped>
.preset-dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 9999px;
  margin-right: 6px;
  vertical-align: 1px;
}
.text-secondary {
  opacity: 0.65;
}
</style>
