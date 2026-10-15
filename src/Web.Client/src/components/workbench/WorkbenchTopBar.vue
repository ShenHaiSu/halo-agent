<template>
  <header
    class="h-[40px] shrink-0 px-3 border-b border-subtle bg-sidebar flex items-center justify-between select-none"
    data-testid="workbench-top-bar"
  >
    <!-- 左侧：工作区 / 会话 路径指示 -->
    <div class="flex items-center gap-2 min-w-0">
      <div class="flex items-center gap-1.5 text-xs text-primary font-medium min-w-0">
        <span class="text-secondary truncate max-w-[140px]" :title="activeWorkspace?.name">
          {{ activeWorkspace?.name || 'Halo' }}
        </span>
        <span class="text-secondary/60">/</span>
        <span class="truncate max-w-[200px]" :title="activeSession?.title">
          {{ activeSession?.title || '未选择会话' }}
        </span>
      </div>
    </div>

    <!-- 右侧：模型与协议选择器 + 上下文水位仪 + 设置与主题 -->
    <div class="flex items-center gap-2.5">
      <!-- 1. 模型与协议选择器 (NSelect 带 CHAT/RESP 徽标) -->
      <div class="w-[280px]">
        <n-select
          :value="selectedModelKey"
          size="small"
          :options="groupedModelOptions"
          :render-label="renderModelOptionLabel"
          placeholder="选择推理模型与协议"
          data-testid="topbar-model-select"
          @update:value="handleSelectModel"
        />
      </div>

      <!-- 2. 上下文预算水位仪 (Context Quota Meter) -->
      <n-popover trigger="hover" placement="bottom-end" style="width: 330px; padding: 0">
        <template #trigger>
          <div
            class="flex items-center gap-2 px-2.5 py-1 rounded bg-surface hover:bg-elevated border border-subtle transition-colors cursor-pointer text-xs"
            data-testid="context-quota-meter-badge"
          >
            <n-progress
              type="circle"
              :percentage="usagePercentage"
              :stroke-width="12"
              :color="statusColor"
              :show-indicator="false"
              :title="`${usagePercentage}% 已用`"
              style="width: 16px; height: 16px; flex-shrink: 0"
            />
            <span class="font-mono text-[11px] font-medium tabular-nums" :class="statusTextColor">
              {{ formatTokens(usedTokens) }} / {{ formatTokens(totalTokens) }}
              <span class="opacity-80">({{ usagePercentage }}%)</span>
            </span>
          </div>
        </template>

        <!-- 悬停详情卡片 -->
        <div class="p-3 space-y-3">
          <div class="flex items-center justify-between border-b border-subtle pb-2">
            <span class="text-xs font-semibold text-primary flex items-center gap-1.5">
              <Layers class="w-3.5 h-3.5 text-accent" />
              <span>上下文预算监控 (Context Budget)</span>
            </span>
            <n-tag size="tiny" :type="usageTagType" :bordered="false" class="font-mono">
              {{ usagePercentage }}% 已用
            </n-tag>
          </div>

          <!-- 水位进度条 -->
          <div class="space-y-1">
            <n-progress
              type="line"
              :percentage="usagePercentage"
              :color="statusColor"
              :height="8"
              :show-indicator="false"
            />
            <div class="flex items-center justify-between text-[10px] text-secondary font-mono">
              <span>0</span>
              <span>安全上限: {{ formatTokens(totalTokens) }}</span>
            </div>
          </div>

          <!-- 指标详情条目 -->
          <div class="space-y-1.5 text-xs bg-surface/60 rounded p-2 border border-subtle/50 font-mono text-[11px]">
            <div class="flex items-center justify-between">
              <span class="text-secondary">已用上下文 (Used)</span>
              <span class="text-primary font-medium">{{ usedTokens.toLocaleString() }} tokens</span>
            </div>
            <div class="flex items-center justify-between">
              <span class="text-secondary">最大输出预留 (Reserve)</span>
              <span class="text-secondary">{{ maxOutputTokens.toLocaleString() }} tokens</span>
            </div>
            <div class="flex items-center justify-between">
              <span class="text-secondary">安全缓冲区 (Safety)</span>
              <span class="text-secondary">{{ safetyBuffer.toLocaleString() }} tokens</span>
            </div>
            <div class="flex items-center justify-between border-t border-subtle/40 pt-1">
              <span class="text-secondary">剩余安全可用 (Available)</span>
              <span class="text-success font-medium">{{ availableTokens.toLocaleString() }} tokens</span>
            </div>
          </div>

          <!-- 会话与协议路由审计 -->
          <div class="space-y-1 text-[11px] pt-1">
            <div class="flex items-center justify-between">
              <span class="text-secondary">当前会话 Hash:</span>
              <div class="flex items-center gap-1">
                <span class="font-mono text-primary font-medium">{{ activeSession?.sessionHash || '-' }}</span>
                <n-button
                  v-if="activeSession?.sessionHash"
                  quaternary
                  circle
                  size="tiny"
                  title="复制 Hash"
                  @click="copySessionHash"
                >
                  <template #icon>
                    <Copy class="w-3 h-3" />
                  </template>
                </n-button>
              </div>
            </div>

            <div class="flex items-center justify-between">
              <span class="text-secondary">推理协议标识:</span>
              <n-tag
                size="tiny"
                :type="activeModel?.protocol === 'resp' ? 'warning' : 'info'"
                :bordered="false"
                class="font-mono"
              >
                {{ activeModel?.protocol === 'resp' ? 'RESP (Responses API)' : 'CHAT (Chat Completions)' }}
              </n-tag>
            </div>
          </div>
        </div>
      </n-popover>

      <!-- 3. 主题切换 -->
      <n-button
        quaternary
        size="small"
        class="!px-2"
        title="切换暗黑/浅色主题"
        @click="appearanceStore.toggleTheme()"
      >
        <template #icon>
          <Sun v-if="appearanceStore.themeMode === 'dark'" class="w-4 h-4" />
          <Moon v-else class="w-4 h-4" />
        </template>
      </n-button>

      <!-- 4. 设置中心入口 -->
      <n-button
        type="primary"
        size="small"
        data-testid="open-settings-btn"
        @click="emit('openSettings')"
      >
        <template #icon>
          <Settings class="w-3.5 h-3.5" />
        </template>
        设置
      </n-button>
    </div>
  </header>
</template>

<script setup lang="ts">
import { computed, h, type VNode } from 'vue';
import { storeToRefs } from 'pinia';
import { useMessage, NTag } from 'naive-ui';
import {
  Layers,
  Copy,
  Settings,
  Sun,
  Moon,
} from 'lucide-vue-next';
import { useAppearanceStore } from '@/stores/appearanceStore';
import { useConfigStore } from '@/stores/configStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import type { ModelConfig } from '@/bridge/hostBridge';

const emit = defineEmits<{
  (e: 'openSettings'): void;
}>();

const message = useMessage();
const appearanceStore = useAppearanceStore();
const configStore = useConfigStore();
const workspaceStore = useWorkspaceStore();

const { activeWorkspace, activeSession, budgetStatus } = storeToRefs(workspaceStore);
const { providersDoc, activeProvider, activeModel } = storeToRefs(configStore);

function formatTokens(count: number): string {
  if (count >= 1000) {
    return `${(count / 1000).toFixed(0)}K`;
  }
  return `${count}`;
}

const totalTokens = computed(() => budgetStatus.value?.totalContextTokens || activeModel.value?.maxContextTokens || 128000);
const maxOutputTokens = computed(() => budgetStatus.value?.maxOutputTokens || activeModel.value?.maxOutputTokens || 4096);
const safetyBuffer = computed(() => budgetStatus.value?.safetyBuffer || 1024);
const usedTokens = computed(() => budgetStatus.value?.usedInputTokens || 0);
const availableTokens = computed(() => budgetStatus.value?.availableInputTokens || Math.max(0, totalTokens.value - maxOutputTokens.value - safetyBuffer.value - usedTokens.value));

const usagePercentage = computed(() => {
  if (budgetStatus.value?.usagePercentage !== undefined) {
    return Math.min(100, Math.max(0, budgetStatus.value.usagePercentage));
  }
  // need04-05：分母统一 maxContext（total），与后端 CalculateBudgetStatus 同公式
  const ratio = usedTokens.value / Math.max(1, totalTokens.value);
  return Math.min(100, Math.round(ratio * 1000) / 10);
});

const statusColor = computed(() => {
  if (usagePercentage.value > 90) return '#f43f5e'; // red
  if (usagePercentage.value >= 70) return '#f59e0b'; // yellow
  return '#3b82f6'; // blue
});

const statusTextColor = computed(() => {
  if (usagePercentage.value > 90) return 'text-red-500';
  if (usagePercentage.value >= 70) return 'text-amber-500';
  return 'text-primary';
});

const usageTagType = computed<'success' | 'warning' | 'error'>(() => {
  if (usagePercentage.value > 90) return 'error';
  if (usagePercentage.value >= 70) return 'warning';
  return 'success';
});

const selectedModelKey = computed(() => {
  if (!activeProvider.value || !activeModel.value) return '';
  return `${activeProvider.value.id}::${activeModel.value.id}`;
});

const groupedModelOptions = computed(() => {
  return providersDoc.value.providers
    .filter((p) => p.enabled)
    .map((p) => ({
      type: 'group',
      label: p.name,
      key: `grp_${p.id}`,
      children: p.models
        .filter((m) => m.enabled)
        .map((m) => ({
          label: m.displayName || m.id,
          value: `${p.id}::${m.id}`,
          model: m,
        })),
    }));
});

function renderModelOptionLabel(option: any): VNode {
  const model: ModelConfig | undefined = option.model;
  if (!model) {
    return h('span', null, option.label);
  }

  const isResp = model.protocol === 'resp';
  const maxCtx = `${Math.round(model.maxContextTokens / 1000)}K`;
  const maxOut = `Out ${Math.round(model.maxOutputTokens / 1000)}K`;

  return h('div', { class: 'flex items-center justify-between w-full gap-2' }, [
    h('span', { class: 'truncate font-medium' }, option.label),
    h('div', { class: 'flex items-center gap-1.5 shrink-0' }, [
      h(
        NTag,
        {
          size: 'tiny',
          bordered: false,
          type: isResp ? 'warning' : 'info',
          class: 'font-mono text-[9px] px-1',
        },
        () => (isResp ? 'RESP' : 'CHAT')
      ),
      h('span', { class: 'text-[10px] text-secondary font-mono' }, `${maxCtx}/${maxOut}`),
    ]),
  ]);
}

async function handleSelectModel(value: string): Promise<void> {
  const [providerId, modelId] = value.split('::');
  if (providerId && modelId) {
    await configStore.setActiveModel(providerId, modelId);
    await workspaceStore.refreshBudget();
    message.success(`已切换模型: ${modelId}`);
  }
}

async function copySessionHash(): Promise<void> {
  if (activeSession.value?.sessionHash) {
    try {
      await navigator.clipboard.writeText(activeSession.value.sessionHash);
      message.success(`已复制 Session Hash: ${activeSession.value.sessionHash}`);
    } catch {
      message.info(`Session Hash: ${activeSession.value.sessionHash}`);
    }
  }
}
</script>
