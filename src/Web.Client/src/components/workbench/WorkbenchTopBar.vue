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
      <n-popover trigger="hover" placement="bottom-end" style="width: 420px; padding: 0">
        <template #trigger>
          <div
            class="flex items-center gap-2 px-2.5 py-1 rounded bg-surface hover:bg-elevated border border-subtle transition-colors cursor-pointer text-xs"
            data-testid="context-quota-meter-badge"
          >
            <n-progress
              type="circle"
              :percentage="progressPercentage"
              :stroke-width="12"
              :color="statusColor"
              :show-indicator="false"
              :title="`${usagePercentage}% 已用`"
              style="width: 16px; height: 16px; flex-shrink: 0"
            />
            <span class="font-mono text-[11px] font-medium tabular-nums" :class="statusTextColor">
              {{ usedDisplay }} / {{ formatTokens(totalTokens) }}
              <span class="opacity-80">({{ percentDisplay }})</span>
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
              {{ percentDisplay }} 已用
            </n-tag>
          </div>

          <!-- 水位进度条 -->
          <div class="space-y-1">
            <n-progress
              type="line"
              :percentage="progressPercentage"
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
              <span class="text-primary font-medium">{{ usedDisplayLong }}</span>
            </div>
            <div class="flex items-center justify-between gap-3">
              <span class="text-secondary shrink-0">计费累计 in/out/Σ</span>
              <span class="text-primary font-medium whitespace-nowrap">{{ billedDisplay }}</span>
            </div>
            <div class="flex items-center justify-between">
              <span class="text-secondary">命中率 cache/input</span>
              <span class="text-primary font-medium">{{ hitRateDisplay }}</span>
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
              <span class="text-secondary">前缀算法：稳定前缀 · 高92%/低70%块淘汰</span>
            </div>
            <div class="flex items-center justify-between pt-1">
              <n-button
                size="tiny"
                :disabled="isCompacting"
                :loading="isCompacting"
                data-testid="compact-session-btn"
                @click="handleCompact"
              >
                {{ isCompacting ? '压缩中…不可中断' : '压缩上下文 (/compact)' }}
              </n-button>
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
import { useAgentStore } from '@/stores/agentStore';
import type { ModelConfig } from '@/bridge/hostBridge';

const emit = defineEmits<{
  (e: 'openSettings'): void;
}>();

const message = useMessage();
const appearanceStore = useAppearanceStore();
const configStore = useConfigStore();
const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();

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
// need05-01：占用态：unknown 画 ?（灰色），绝不画 0% 误导；空会话 known+0% 画蓝色 0%
const occupancyState = computed(() => budgetStatus.value?.occupancyState || 'known');
const lastPromptTokens = computed(() => budgetStatus.value?.lastPromptTokens ?? null);
const isUnknown = computed(() => {
  if (usedTokens.value === 0 && occupancyState.value === 'known') return false; // 空会话
  return occupancyState.value === 'unknown' || lastPromptTokens.value === null;
});
const liveEst = computed(() => agentStore.telemetry.liveEst || 0);
const usedDisplay = computed(() => {
  if (isUnknown.value) return '?';
  const base = formatTokens(usedTokens.value);
  return liveEst.value > 0 ? `${base}≈+${formatTokens(liveEst.value)}` : base; // need05-01：流式中 ≈ 估算
});
const usedDisplayLong = computed(() => {
  if (isUnknown.value) return '占用未知 (?)';
  const base = `${usedTokens.value.toLocaleString()} tokens`;
  return liveEst.value > 0 ? `${base} ≈+${liveEst.value.toLocaleString()}` : base;
});
const percentDisplay = computed(() => (isUnknown.value ? '?' : `${usagePercentage.value}%`));
const progressPercentage = computed(() => (isUnknown.value ? 0 : usagePercentage.value));
// need05-01：悬停卡计费累计与命中率
// 计费单位强制为 K（千）/ M（百万），最多两位小数，避免横向过长换行
function formatCompact(count: number): string {
  const abs = Math.abs(count);
  const trim = (v: number): string => {
    const s = v.toFixed(2);
    return s.includes('.') ? s.replace(/0+$/, '').replace(/\.$/, '') : s;
  };
  if (abs >= 1000000) return `${trim(count / 1000000)}M`;
  return `${trim(count / 1000)}K`;
}
const billedDisplay = computed(() => {
  const p = budgetStatus.value?.billedPromptTokens;
  const c = budgetStatus.value?.billedCompletionTokens;
  if (p === undefined || p === null || c === undefined || c === null) return '-';
  return `${formatCompact(p)} / ${formatCompact(c)} / ${formatCompact(p + c)}`;
});
const hitRateDisplay = computed(() => {
  const r = budgetStatus.value?.cacheHitRate;
  if (r === undefined || r === null) return '-';
  return `${Math.round(r * 100)}%`;
});
// need05-03：压缩忙态（压缩中不可取消，按钮置灰）
const isCompacting = computed(() => {
  const sid = activeSession.value?.id || '';
  if (!sid) return false;
  try {
    return agentStore.isCompacting(sid);
  } catch {
    return false;
  }
});
async function handleCompact(): Promise<void> {
  const sid = activeSession.value?.id || '';
  if (!sid || isCompacting.value) return;
  const ok = await workspaceStore.compactSession(sid);
  if (!ok) message.warning('压缩启动失败，请稍后重试');
}
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
  if (isUnknown.value) return '#6b7280'; // need05-01：未知灰色
  if (usagePercentage.value > 90) return '#f43f5e'; // red
  if (usagePercentage.value >= 70) return '#f59e0b'; // yellow
  return '#3b82f6'; // blue
});

const statusTextColor = computed(() => {
  if (isUnknown.value) return 'text-secondary';
  if (usagePercentage.value > 90) return 'text-red-500';
  if (usagePercentage.value >= 70) return 'text-amber-500';
  return 'text-primary';
});

const usageTagType = computed<'success' | 'warning' | 'error' | 'default'>(() => {
  if (isUnknown.value) return 'default';
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
