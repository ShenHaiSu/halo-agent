<template>
  <div class="space-y-3 min-w-0 max-w-full overflow-x-hidden" data-testid="agent-behavior-tab">
    <div
      data-testid="agent-action-bar"
      class="sticky top-0 z-10 flex items-center gap-2 rounded-md border border-subtle bg-elevated px-3 py-2 text-xs"
    >
      <span v-if="agentStore.dirty">●脏态</span><span v-else>○干净</span>
      <n-button
        type="primary"
        size="small"
        data-testid="agent-save"
        :loading="agentStore.saving"
        :disabled="!agentStore.dirty || agentStore.saving"
        @click="onSave"
        >保存</n-button
      >
      <n-button size="small" type="error" ghost data-testid="agent-reset-all" @click="onResetAll">恢复全部默认</n-button>
      <span v-if="agentStore.config?.fallbackReason">{{ agentStore.config?.fallbackReason }}</span>
      <span data-testid="agent-section-nav" class="ml-auto text-secondary">超时 · 轮次 · 子代理 · 监控 · 拒绝</span>
    </div>
    <n-alert v-if="showIntro" type="info" closable class="text-xs" @close="showIntro = false"
      >生效范围：全局 config/agent.json，保存后下一轮对话起生效。</n-alert
    >
    <div class="grid grid-cols-1 xl:grid-cols-2 gap-3">
      <AgentTimeoutCard v-if="agentStore.config" :cfg="agentStore.config" @update="onNum" @reset="onResetField" />
      <AgentIterationsCard v-if="agentStore.config" :cfg="agentStore.config" @update="onNum" @reset="onResetField" />
      <SubAgentConfigCard v-if="agentStore.config" :cfg="agentStore.config" @bool="onBool" @num="onSubNum" @reset="onResetField" />
    </div>
    <div class="grid grid-cols-1 gap-3">
      <SubRuntimeMonitorCard
        :rows="subRuntimeRows"
        :loading="subRuntimeLoading"
        :policy-text="subPolicyText"
        :rejection="subRejection"
        @refresh="onSubRuntimeRefresh"
        @cancel-tree="onSubCancelTree"
        @open-history="onOpenHistory"
      />
      <RejectionLogCard :rows="rejectionRows" />
    </div>
  </div>
</template>
<script setup lang="ts">
/**
 * need23：智能体行为页。
 * - 移除"目标摘要"卡（目标是会话级只读状态，编辑入口在右侧栏；此处展示无价值且跳转是死链）。
 * - 补齐 SubAgentConfigCard / SubRuntimeMonitorCard 的 import（此前漏 import → 整卡不渲染）。
 * - 子会话 id 不再截断（截断后 enterSub 必然找不到子会话）。
 */
import { ref, onMounted, onUnmounted } from 'vue';
import { useMessage, useDialog } from 'naive-ui';
import { useAgentConfigStore } from '@/stores/agentConfigStore';
import { getHostBridge, type AgentConfigDocument, type SubRuntimeInfo } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSettingsDrawer } from '../composables/useSettingsDrawer';
import AgentTimeoutCard from './agent/cards/AgentTimeoutCard.vue';
import AgentIterationsCard from './agent/cards/AgentIterationsCard.vue';
import SubAgentConfigCard from './agent/cards/SubAgentConfigCard.vue';
import SubRuntimeMonitorCard from './agent/cards/SubRuntimeMonitorCard.vue';
import RejectionLogCard from './agent/cards/RejectionLogCard.vue';
import { clampSubNum, type SubBoolField, type SubNumField } from './agent/cards/subAgentFields';
import type { SubRejectionRow, SubRuntimeRow } from './agent/cards/subRuntimeTypes';

const message = useMessage();
const dialog = useDialog();
const agentStore = useAgentConfigStore();
const workspaceStore = useWorkspaceStore();
const settingsDrawer = useSettingsDrawer();
const showIntro = ref(false);
try {
  showIntro.value = localStorage.getItem('halo-agent-intro') !== 'hide';
} catch {
  showIntro.value = false;
}

const subRuntimeLoading = ref(false);
const subRuntimeRows = ref<SubRuntimeRow[]>([]);
const subPolicyText = ref('');
const subRejection = ref<SubRejectionRow | null>(null);
const rejectionRows = ref<SubRejectionRow[]>([]);

/** 超时/轮次数值字段（AgentTimeoutCard + AgentIterationsCard）。 */
type NumericField = 'execDefaultTimeoutMs' | 'execMaxTimeoutMs' | 'maxIterations' | 'turnTimeoutMs';

function setConfigField<K extends keyof AgentConfigDocument>(field: K, value: AgentConfigDocument[K]): void {
  const c = agentStore.config;
  if (!c) return;
  c[field] = value;
  agentStore.touchDirty();
}

function onNum(field: NumericField, v: number | null): void {
  setConfigField(field, v ?? 0);
}

function onBool(field: SubBoolField, v: boolean): void {
  setConfigField(field, v);
}

/** 子代理数值：写入前裁剪到后端允许区间（1..8 / 1..100），避免 0 触发 AGENT_VALIDATION。 */
function onSubNum(field: SubNumField, v: number | null): void {
  setConfigField(field, clampSubNum(field, v));
}

async function onSave() {
  const res = await agentStore.saveConfig();
  if (res.ok) message.success('智能体行为配置已保存，下一轮对话起生效');
  else message.error(res.message || '保存失败');
}

function onResetAll() {
  dialog.error({
    title: '恢复全部默认',
    content: '确认恢复全部默认？',
    positiveText: '确认恢复',
    negativeText: '取消',
    onPositiveClick: async () => {
      const r = await agentStore.resetAll();
      if (r.ok) message.success('已恢复全部默认');
      else message.error('恢复失败');
    },
  });
}

/** 恢复单项默认（字段名即后端 reset 别名，后端大小写/别名均认）。 */
async function onResetField(field: string): Promise<void> {
  const res = await agentStore.resetField(field);
  if (res.ok) message.success('已恢复本项默认');
  else message.error(res.message || '恢复失败');
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
      return;
    }
    const res = await bridge.getSubRuntime(wsId, parentId);
    if (!res.ok || !res.data) return;
    const d = res.data as SubRuntimeInfo;
    subRuntimeRows.value = (d.subs || []).map((s) => ({
      // need23：完整 id（进入子会话要用）；展示用短 id 单独放 shortId
      subId: String(s.subId),
      shortId: String(s.subId).slice(0, 8),
      label: s.label,
      status: s.status,
      iteration: s.iteration,
      toolCount: s.toolCount,
      lastTool: s.lastTool,
      elapsedMs: s.elapsedMs,
    }));
    const p = d.policy;
    subPolicyText.value = p ? `快照：${p.enabled ? '开' : '关'} / 并发${p.maxParallel} / 轮次${p.maxIterations}` : '';
    // need23：拒绝记录用后端真实时间（旧实现前端伪造 now()）
    const list = d.recentRejections || (d.lastRejection ? [d.lastRejection] : []);
    rejectionRows.value = list.map((r) => ({ code: r.code, callId: r.callId, atUtc: r.atUtc }));
    subRejection.value = rejectionRows.value.length > 0 ? rejectionRows.value[0] : null;
  } catch (e) {
    console.warn('[agent-behavior] getSubRuntime failed:', e);
  } finally {
    subRuntimeLoading.value = false;
  }
}

function onSubCancelTree(): void {
  dialog.error({
    title: '掐断整树',
    content: '确认掐断整树？',
    positiveText: '确认掐断',
    negativeText: '取消',
    onPositiveClick: async () => {
      const bridge = getHostBridge();
      const parentId = workspaceStore.activeSessionId || '';
      if (!parentId) return;
      const res = await bridge.cancelAgentTree(parentId);
      if (res.ok) {
        message.success('已发送整树取消指令');
        void onSubRuntimeRefresh();
      } else {
        message.error(res.message || '取消失败');
      }
    },
  });
}

/** 进入子会话：必须用完整 subId（截断过的 id 后端查不到）。 */
async function onOpenHistory(subId: string): Promise<void> {
  const wsId = workspaceStore.activeWorkspaceId || '';
  const parent = workspaceStore.activeSessionId || '';
  const target = (subId || '').trim();
  if (!wsId || !parent || !target) {
    message.info('当前无可进入的子会话');
    return;
  }
  try {
    const { useSubSessionStore } = await import('@/stores/subSessionStore');
    const s = useSubSessionStore();
    await s.enterSub(wsId, parent, target);
    message.success('已进入子会话');
    // need23：让出主工作台（否则设置抽屉盖住子会话视图）
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
