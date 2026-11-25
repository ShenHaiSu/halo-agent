<template>
  <div class="agent-behavior min-w-0 max-w-full overflow-x-hidden" data-testid="agent-behavior-tab">
    <AgentBehaviorActionBar
      :dirty="agentStore.dirty"
      :dirty-count="agentStore.dirtyFields.length"
      :saving="agentStore.saving"
      :all-open="cards.allOpen.value"
      @save="onSave"
      @discard="onDiscard"
      @reset-all="onResetAll"
      @toggle-all="cards.toggleAll()"
    />

    <n-alert
      v-if="validationMessage"
      type="error"
      :show-icon="true"
      closable
      class="agent-inline-alert"
      data-testid="agent-validation-alert"
      @close="validationMessage = ''"
    >
      {{ validationMessage }}
    </n-alert>

    <n-alert v-if="agentStore.externalChangePending" type="warning" :show-icon="true" class="agent-inline-alert">
      <template #default>
        <div>配置已在外部更改。请先保存或放弃当前修改，再重新加载。</div>
        <div class="agent-inline-alert-actions">
          <n-button size="tiny" @click="onDiscard">放弃当前修改并加载外部配置</n-button>
          <n-button size="tiny" @click="onKeepLocal">保留当前修改，暂不加载</n-button>
        </div>
      </template>
    </n-alert>

    <AgentBehaviorCard
      title="命令执行超时"
      :summary="summaries.timeout"
      :open="cards.expanded.timeout"
      :dirty="cards.isCardDirty('timeout', agentStore.dirtyFields)"
      test-id="agent-card-timeout"
      @update:open="(v: boolean) => cards.setOpen('timeout', v)"
    >
      <template #header-extra>
        <n-button
          size="tiny"
          quaternary
          :loading="resettingCard === 'timeout'"
          :disabled="agentStore.saving || resettingCard !== null"
          @click="onResetCard('timeout')"
          >恢复本卡默认</n-button
        >
      </template>
      <AgentTimeoutCard
        v-if="agentStore.config"
        :cfg="agentStore.config"
        :dirty-fields="agentStore.dirtyFields"
        @update="onNum"
        @reset="onResetField"
      />
    </AgentBehaviorCard>

    <AgentBehaviorCard
      title="工具轮次与单轮时限"
      :summary="summaries.iterations"
      :open="cards.expanded.iterations"
      :dirty="cards.isCardDirty('iterations', agentStore.dirtyFields)"
      test-id="agent-card-iterations"
      @update:open="(v: boolean) => cards.setOpen('iterations', v)"
    >
      <template #header-extra>
        <n-button
          size="tiny"
          quaternary
          :loading="resettingCard === 'iterations'"
          :disabled="agentStore.saving || resettingCard !== null"
          @click="onResetCard('iterations')"
          >恢复本卡默认</n-button
        >
      </template>
      <AgentIterationsCard
        v-if="agentStore.config"
        :cfg="agentStore.config"
        :dirty-fields="agentStore.dirtyFields"
        @update="onNum"
        @reset="onResetField"
      />
    </AgentBehaviorCard>

    <AgentBehaviorCard
      title="子代理配置"
      :summary="summaries.subagents"
      :open="cards.expanded.subagents"
      :dirty="cards.isCardDirty('subagents', agentStore.dirtyFields)"
      test-id="agent-card-subagents"
      @update:open="(v: boolean) => cards.setOpen('subagents', v)"
    >
      <template #header-extra>
        <n-button
          size="tiny"
          quaternary
          :loading="resettingCard === 'subagents'"
          :disabled="agentStore.saving || resettingCard !== null"
          @click="onResetCard('subagents')"
          >恢复本卡默认</n-button
        >
      </template>
      <SubAgentConfigCard
        v-if="agentStore.config"
        :cfg="agentStore.config"
        :dirty-fields="agentStore.dirtyFields"
        @bool="onBool"
        @num="onSubNum"
        @reset="onResetField"
      />
    </AgentBehaviorCard>

    <AgentBehaviorCard
      title="子运行监控"
      :summary="summaries.monitor"
      :open="cards.expanded.monitor"
      test-id="agent-card-monitor"
      @update:open="(v: boolean) => cards.setOpen('monitor', v)"
    >
      <SubRuntimeMonitorCard
        :rows="subRuntimeRows"
        :loading="subRuntimeLoading"
        :policy-text="subPolicyText"
        :rejection="subRejection"
        @refresh="onSubRuntimeRefresh"
        @cancel-tree="onSubCancelTree"
        @open-history="onOpenHistory"
        @view-rejection="onViewRejection"
      />
    </AgentBehaviorCard>

    <AgentBehaviorCard
      title="拒绝与自愈记录"
      :summary="summaries.rejection"
      :open="cards.expanded.rejection"
      test-id="agent-card-rejection"
      @update:open="(v: boolean) => cards.setOpen('rejection', v)"
    >
      <RejectionLogCard :rows="rejectionRows" />
    </AgentBehaviorCard>
  </div>
</template>
<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';
import { useMessage, useDialog } from 'naive-ui';
import { useAgentConfigStore } from '@/stores/agentConfigStore';
import { getHostBridge, type AgentConfigDocument, type SubRuntimeInfo } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSettingsDrawer } from '../composables/useSettingsDrawer';
import AgentBehaviorActionBar from './agent/AgentBehaviorActionBar.vue';
import AgentBehaviorCard from './agent/AgentBehaviorCard.vue';
import AgentTimeoutCard from './agent/cards/AgentTimeoutCard.vue';
import AgentIterationsCard from './agent/cards/AgentIterationsCard.vue';
import SubAgentConfigCard from './agent/cards/SubAgentConfigCard.vue';
import SubRuntimeMonitorCard from './agent/cards/SubRuntimeMonitorCard.vue';
import RejectionLogCard from './agent/cards/RejectionLogCard.vue';
import { clampSubNum, type SubBoolField, type SubNumField } from './agent/cards/subAgentFields';
import type { SubRejectionRow, SubRuntimeRow } from './agent/cards/subRuntimeTypes';
import { useAgentBehaviorCards } from './agent/composables/useAgentBehaviorCards';
import { validateAgentBehavior } from './agent/composables/useAgentBehaviorValidation';
import { formatDuration, formatPermissionSummary } from './agent/utils/agentBehaviorFormat';
import { fieldToCard, type AgentBehaviorCardName } from './agent/agentBehaviorTypes';
import './agent/agent-behavior.css';

const message = useMessage();
const dialog = useDialog();
const agentStore = useAgentConfigStore();
const workspaceStore = useWorkspaceStore();
const settingsDrawer = useSettingsDrawer();
const cards = useAgentBehaviorCards();

const validationMessage = ref('');
const resettingCard = ref<AgentBehaviorCardName | null>(null);
const subRuntimeLoading = ref(false);
const subRuntimeRows = ref<SubRuntimeRow[]>([]);
const subPolicyText = ref('');
const subRejection = ref<SubRejectionRow | null>(null);
const rejectionRows = ref<SubRejectionRow[]>([]);

/** 整卡恢复：后端没有分组别名，必须串行单项 reset，避免原子写回互相覆盖。 */
const CARD_RESET_ALIASES: Record<AgentBehaviorCardName, readonly string[]> = {
  timeout: ['defaultTimeout', 'maxTimeout'],
  iterations: ['iterations', 'turnTimeout'],
  subagents: ['subEnabled', 'subParallel', 'subIterations', 'subPerms'],
  monitor: [],
  rejection: [],
};

type NumericField = 'execDefaultTimeoutMs' | 'execMaxTimeoutMs' | 'maxIterations' | 'turnTimeoutMs';

const summaries = computed(() => {
  const cfg = agentStore.config;
  const toolCalls = subRuntimeRows.value.reduce((total, row) => total + (row.toolCount || 0), 0);
  return {
    timeout: cfg ? `默认 ${formatDuration(cfg.execDefaultTimeoutMs)} · 上限 ${formatDuration(cfg.execMaxTimeoutMs)}` : '',
    iterations: cfg ? `${cfg.maxIterations} 轮 · 单轮 ${formatDuration(cfg.turnTimeoutMs ?? 600000)}` : '',
    subagents: cfg ? `${cfg.subAgentEnabled ? '已启用' : '已关闭'} · 并发 ${cfg.subMaxParallel} · 每子 ${cfg.subMaxIterations} 轮` : '',
    monitor: `${subRuntimeRows.value.length} 个运行中 · ${toolCalls} 次工具调用`,
    rejection: `${rejectionRows.value.length} 条记录`,
  };
});

function setConfigField<K extends keyof AgentConfigDocument>(field: K, value: AgentConfigDocument[K]): void {
  const cfg = agentStore.config;
  if (!cfg) return;
  cfg[field] = value;
  agentStore.touchDirty();
}

function onNum(field: NumericField, v: number | null): void {
  setConfigField(field, v ?? 0);
}

function onBool(field: SubBoolField, v: boolean): void {
  setConfigField(field, v);
}

function onSubNum(field: SubNumField, v: number | null): void {
  setConfigField(field, clampSubNum(field, v));
}

/** 展开出错字段所属卡片并把焦点移到控件，不只靠 toast。 */
async function focusAgentField(field: string): Promise<void> {
  cards.setOpen(fieldToCard(field), true);
  await nextTick();
  const host = document.querySelector<HTMLElement>(`[data-agent-field="${field}"]`);
  if (!host) return;
  const control = host.querySelector<HTMLElement>('input, textarea, button') ?? host;
  control.focus();
  host.scrollIntoView?.({ block: 'nearest' });
}

async function onSave(): Promise<void> {
  const cfg = agentStore.config;
  if (!cfg) return;
  validationMessage.value = '';
  const issues = validateAgentBehavior(cfg);
  if (issues.length > 0) {
    validationMessage.value = issues[0].message;
    await focusAgentField(issues[0].key);
    message.error(issues[0].message);
    return;
  }
  const res = await agentStore.saveConfig();
  if (res.ok) {
    message.success('智能体行为配置已保存，下一轮对话起生效');
    return;
  }
  if (res.code === 'AGENT_VALIDATION' && agentStore.lastError) {
    validationMessage.value = agentStore.lastError.message;
    await focusAgentField(agentStore.lastError.field);
    message.error(agentStore.lastError.message);
    return;
  }
  message.error(res.message || '保存失败');
}

async function onDiscard(): Promise<void> {
  const hadExternalChange = agentStore.externalChangePending;
  agentStore.discardChanges();
  validationMessage.value = '';
  if (hadExternalChange) await agentStore.loadConfig();
}

function onKeepLocal(): void {
  agentStore.externalChangePending = false;
}

function onResetAll(): void {
  dialog.warning({
    title: '恢复全部默认',
    content: '恢复后立即写入本地配置，下一轮对话起生效。确认恢复全部默认？',
    positiveText: '确认恢复',
    negativeText: '取消',
    onPositiveClick: async () => {
      const res = await agentStore.resetAll();
      if (res.ok) message.success('已恢复全部默认');
      else message.error(res.message || '恢复失败');
    },
  });
}

async function onResetCard(card: AgentBehaviorCardName): Promise<void> {
  if (resettingCard.value !== null) return;
  resettingCard.value = card;
  try {
    for (const alias of CARD_RESET_ALIASES[card]) {
      const res = await agentStore.resetField(alias);
      if (!res.ok) {
        message.error(res.message || '恢复失败');
        return;
      }
    }
    validationMessage.value = '';
    message.success('已恢复本卡默认');
  } finally {
    resettingCard.value = null;
  }
}

async function onResetField(field: string): Promise<void> {
  const res = await agentStore.resetField(field);
  if (res.ok) {
    validationMessage.value = '';
    message.success('已恢复本项默认');
  } else {
    message.error(res.message || '恢复失败');
  }
}

async function onSubRuntimeRefresh(): Promise<void> {
  subRuntimeLoading.value = true;
  try {
    const bridge = getHostBridge();
    const wsId = workspaceStore.activeWorkspaceId || '';
    const parentId = workspaceStore.activeSessionId || '';
    if (!wsId || !parentId) {
      subRuntimeRows.value = [];
      rejectionRows.value = [];
      subRejection.value = null;
      subPolicyText.value = '';
      return;
    }
    const res = await bridge.getSubRuntime(wsId, parentId);
    if (!res.ok || !res.data) return;
    const data = res.data as SubRuntimeInfo;
    subRuntimeRows.value = (data.subs || []).map((item) => ({
      subId: String(item.subId),
      shortId: String(item.subId).slice(0, 8),
      label: item.label,
      status: item.status,
      iteration: item.iteration,
      toolCount: item.toolCount,
      lastTool: item.lastTool,
      elapsedMs: item.elapsedMs,
    }));
    const policy = data.policy;
    subPolicyText.value = policy
      ? `快照：${policy.enabled ? '已启用' : '已关闭'} · 并发 ${policy.maxParallel} · 每子 ${policy.maxIterations} 轮 · ${formatPermissionSummary(policy)}`
      : '';
    const list = data.recentRejections?.length ? data.recentRejections : data.lastRejection ? [data.lastRejection] : [];
    rejectionRows.value = list.map((item) => ({ code: item.code, callId: item.callId, atUtc: item.atUtc }));
    subRejection.value = rejectionRows.value.length > 0 ? rejectionRows.value[0] : null;
  } catch (e) {
    console.warn('[agent-behavior] getSubRuntime failed:', e);
  } finally {
    subRuntimeLoading.value = false;
  }
}

function onSubCancelTree(): void {
  dialog.warning({
    title: '掐断整树',
    content: '确认掐断整树？运行中的子代理任务将被取消。',
    positiveText: '确认掐断',
    negativeText: '取消',
    onPositiveClick: async () => {
      const parentId = workspaceStore.activeSessionId || '';
      if (!parentId) return;
      const res = await getHostBridge().cancelAgentTree(parentId);
      if (res.ok) {
        message.success('已发送整树取消指令');
        void onSubRuntimeRefresh();
      } else {
        message.error(res.message || '取消失败');
      }
    },
  });
}

function onViewRejection(): void {
  cards.setOpen('rejection', true);
  void nextTick(() => {
    document.querySelector('[data-testid="agent-card-rejection"]')?.scrollIntoView?.({ block: 'nearest' });
  });
}

async function onOpenHistory(subId: string): Promise<void> {
  const wsId = workspaceStore.activeWorkspaceId || '';
  const parentId = workspaceStore.activeSessionId || '';
  const target = (subId || '').trim();
  if (!wsId || !parentId || !target) {
    message.info('当前无可进入的子会话');
    return;
  }
  try {
    const { useSubSessionStore } = await import('@/stores/subSessionStore');
    const store = useSubSessionStore();
    await store.enterSub(wsId, parentId, target);
    message.success('已进入子会话');
    settingsDrawer.close();
  } catch (e) {
    message.error(`进入子会话失败：${String((e as Error)?.message ?? e)}`);
  }
}

let timer: number | null = null;
onMounted(async () => {
  agentStore.watchStorageChange();
  if (!agentStore.config) await agentStore.loadConfig();
  void onSubRuntimeRefresh();
  timer = window.setInterval(() => {
    void onSubRuntimeRefresh();
  }, 5000);
});
onUnmounted(() => {
  if (timer !== null) clearInterval(timer);
});
</script>
