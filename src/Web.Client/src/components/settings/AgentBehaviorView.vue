<template>
  <div class="space-y-3 min-w-0 max-w-full overflow-x-hidden" data-testid="agent-behavior-tab">
    <div data-testid="agent-action-bar" class="sticky top-0 z-10 flex items-center gap-2 rounded-md border border-subtle bg-elevated px-3 py-2 text-xs">
      <span v-if="agentStore.dirty">●脏态</span><span v-else>○干净</span>
      <n-button type="primary" size="small" data-testid="agent-save" :loading="agentStore.saving" :disabled="!agentStore.dirty || agentStore.saving" @click="onSave">保存</n-button>
      <n-button size="small" type="error" ghost data-testid="agent-reset-all" @click="onResetAll">恢复全部默认</n-button>
      <span v-if="agentStore.config?.fallbackReason">{{ agentStore.config?.fallbackReason }}</span>
      <span data-testid="agent-section-nav" class="ml-auto text-secondary">超时 · 轮次 · 子代理 · 目标 · 监控 · 拒绝</span>
    </div>
    <n-alert v-if="showIntro" type="info" closable class="text-xs" @close="showIntro = false">生效范围：全局 config/agent.json，保存后下一轮对话起生效。</n-alert>
    <div class="grid grid-cols-1 xl:grid-cols-2 gap-3">
      <AgentTimeoutCard v-if="agentStore.config" :cfg="agentStore.config" @update="onNum" @reset="onResetField" />
      <AgentIterationsCard v-if="agentStore.config" :cfg="agentStore.config" @update="onNum" @reset="onResetField" />
      <SubAgentConfigCard v-if="agentStore.config" :cfg="(agentStore.config as unknown as Record<string, number | boolean>)" @bool="onBool" @num="onNum" @reset="onResetField" />
      <GoalSummaryCard :goal="goal" @jump="onGoalJump" />
    </div>
    <div class="grid grid-cols-1 gap-3">
       <SubRuntimeMonitorCard :rows="subRuntimeRows" :columns="subRuntimeColumns" :loading="subRuntimeLoading" :policy-text="subPolicyText" :rejection="subRejection" @refresh="onSubRuntimeRefresh" @cancel-tree="onSubCancelTree" @open-history="onOpenHistory" />
      <RejectionLogCard :rows="rejectionRows" />
    </div>
  </div>
</template>
<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
import { useMessage, useDialog } from 'naive-ui';
import { useAgentConfigStore } from '@/stores/agentConfigStore';
import { getHostBridge, type SubRuntimeInfo, type SessionGoalDto } from '@/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useGoalStore } from '@/stores/goalStore';
import { useSubSessionStore } from '@/stores/subSessionStore';
import AgentTimeoutCard from './agent/cards/AgentTimeoutCard.vue';
import AgentIterationsCard from './agent/cards/AgentIterationsCard.vue';
import GoalSummaryCard from './agent/cards/GoalSummaryCard.vue';
import RejectionLogCard from './agent/cards/RejectionLogCard.vue';

const message = useMessage();
const dialog = useDialog();
const agentStore = useAgentConfigStore();
const workspaceStore = useWorkspaceStore();
const goalStore = useGoalStore();
const showIntro = ref(false);
try { showIntro.value = localStorage.getItem('halo-agent-intro') !== 'hide'; } catch { showIntro.value = false; }
const goal = computed<SessionGoalDto | null>(() => goalStore.goal);
const subRuntimeLoading = ref(false);
const subRuntimeRows = ref<Array<{ subId: string; label: string; status: string; iteration: number; tool: string; elapsedMs: number }>>([]);
const subPolicyText = ref('');
const subRejection = ref<{ code: string; callId: string } | null>(null);
const rejectionRows = computed(() => subRejection.value ? [{ code: subRejection.value.code, callId: subRejection.value.callId, atUtc: new Date().toISOString() }] : []);
const subRuntimeColumns = [
  { title: 'label', key: 'label', width: 140, ellipsis: true },
  { title: 'status', key: 'status', width: 90 },
  { title: '耗时ms', key: 'elapsedMs', width: 100 },
  { title: '工具', key: 'tool', width: 140, ellipsis: true },
  { title: '操作', key: 'actions', width: 120, render: (row: { subId: string }) => `历史 ${String(row.subId).slice(0, 8)}` },
];

function onNum(field: string, v: number | null) {
  const c = agentStore.config; if (!c) return;
  (c as unknown as Record<string, unknown>)[field] = v ?? 0;
  agentStore.touchDirty();
}
function onBool(field: string, v: boolean) {
  const c = agentStore.config; if (!c) return;
  (c as unknown as Record<string, unknown>)[field] = v;
  agentStore.touchDirty();
}
async function onSave() {
  const res = await agentStore.saveConfig();
  if ((res as unknown as { ok: boolean }).ok) message.success('智能体行为配置已保存，下一轮对话起生效');
  else message.error((res as unknown as { message?: string }).message || '保存失败');
}
function onResetAll() {
  dialog.error({ title: '恢复全部默认', content: '确认恢复全部默认？', positiveText: '确认恢复', negativeText: '取消', onPositiveClick: async () => { const r = await agentStore.resetAll(); if ((r as unknown as { ok: boolean }).ok) message.success('已恢复全部默认'); else message.error('恢复失败'); } });
}
async function onResetField(field: string) {
  const map: Record<string, string> = { defaultTimeout: 'defaultTimeout', maxTimeout: 'maxTimeout', iterations: 'iterations', subEnabled: 'subEnabled', subParallel: 'subParallel', subIterations: 'subIterations', subPerms: 'subPerms' };
  const res = await agentStore.resetField(map[field] || field);
  if ((res as unknown as { ok: boolean }).ok) message.success('已恢复本项默认'); else message.error('恢复失败');
}
async function onSubRuntimeRefresh() {
  subRuntimeLoading.value = true;
  try {
    const bridge = getHostBridge();
    const wsId = workspaceStore.activeWorkspaceId || '';
    const parentId = workspaceStore.activeSessionId || '';
    if (!wsId || !parentId) { subRuntimeRows.value = []; return; }
    const res = await bridge.getSubRuntime(wsId, parentId);
    if (res.ok && res.data) {
      const d = res.data as SubRuntimeInfo;
      subRuntimeRows.value = (d.subs || []).map((s) => ({ subId: String(s.subId).slice(0, 8), label: s.label, status: s.status, iteration: s.iteration, tool: s.tool, elapsedMs: s.elapsedMs }));
      const p = d.policy;
      subPolicyText.value = p ? `快照：${p.enabled ? '开' : '关'} / 并发${p.maxParallel} / 轮次${p.maxIterations}` : '';
      const rej = (d.recentRejections && d.recentRejections[0]) || d.lastRejection;
      subRejection.value = rej ? { code: rej.code, callId: rej.callId } : null;
      const ws2 = workspaceStore.activeWorkspaceId || ''; const sess = workspaceStore.activeSessionId || '';
      if (ws2 && sess) { const g = await bridge.getGoal(ws2, sess); if (g.ok && g.data) goalStore.goal = g.data; }
    }
  } finally { subRuntimeLoading.value = false; }
}
function onSubCancelTree() {
  dialog.error({ title: '掐断整树', content: '确认掐断整树？', positiveText: '确认掐断', negativeText: '取消', onPositiveClick: async () => { const bridge = getHostBridge(); const parentId = workspaceStore.activeSessionId || ''; if (!parentId) return; const res = await bridge.cancelAgentTree(parentId); if (res.ok) { message.success('已发送整树取消指令'); void onSubRuntimeRefresh(); } else message.error(res.message || '取消失败'); } });
}
 function onGoalJump() { window.dispatchEvent(new CustomEvent('halo:goal-jump')); }
 async function onOpenHistory(subId: string) {
   try {
     const { useSubSessionStore } = await import('@/stores/subSessionStore');
     const s = useSubSessionStore();
     const wsId = workspaceStore.activeWorkspaceId || ''; const parent = workspaceStore.activeSessionId || '';
     let target = (subId || '').trim();
     if (!target) { const first = s.subs[0] as unknown as { id?: string; subId?: string } | undefined; target = String(first?.id || (first as unknown as { subId?: string })?.subId || ''); if (!target && subRuntimeRows.value[0]) target = String((subRuntimeRows.value[0] as Record<string, unknown>).subId || ''); }
     if (wsId && parent && target) await s.enterSub(wsId, parent, target);
     else message.info('当前无可进入的子会话');
   } catch { /* ignore */ }
 }
let timer: number | null = null;
onMounted(async () => {
  agentStore.watchStorageChange();
  if (!agentStore.config) await agentStore.loadConfig();
  void onSubRuntimeRefresh();
  timer = window.setInterval(() => { void onSubRuntimeRefresh(); }, 5000);
});
onUnmounted(() => { if (timer !== null) clearInterval(timer); });
</script>
