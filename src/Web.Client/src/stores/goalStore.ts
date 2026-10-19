import { defineStore } from 'pinia';
import { ref, computed, watch } from 'vue';
import { getHostBridge, haloEventBus, type SessionGoalDto } from '@/bridge/hostBridge';
import { useWorkspaceStore } from './workspaceStore';

export type GoalPanelState = 'expanded' | 'collapsed' | 'mini';

export const useGoalStore = defineStore('goal', () => {
  const goal = ref<SessionGoalDto | null>(null);
  const loading = ref(false);
  const corrupted = ref(false);
  const panel = ref<GoalPanelState>('mini');
  const pos = ref({ right: 16, bottom: 88 });
  const updatedFlash = ref(false);

  try {
    const saved = localStorage.getItem('halo-goal-panel-state');
    if (saved === 'expanded' || saved === 'collapsed' || saved === 'mini') panel.value = saved;
    const p = localStorage.getItem('halo-goal-panel-pos');
    if (p) { const o = JSON.parse(p); if (typeof o.right === 'number' && typeof o.bottom === 'number') pos.value = o; }
  } catch { /* ignore */ }

  const pct = computed(() => goal.value?.percent ?? 0);
  const done = computed(() => (goal.value?.milestones || []).filter((m) => m.done).length);
  const total = computed(() => (goal.value?.milestones || []).length);

  function setPanel(s: GoalPanelState) {
    panel.value = s;
    try { localStorage.setItem('halo-goal-panel-state', s); } catch { /* ignore */ }
  }
  function movePos(right: number, bottom: number) {
    pos.value = { right, bottom };
    try { localStorage.setItem('halo-goal-panel-pos', JSON.stringify(pos.value)); } catch { /* ignore */ }
  }

  async function load(ws: string, sess: string) {
    if (!ws || !sess) { goal.value = null; return; }
    loading.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getGoal(ws, sess);
      if (res.ok && res.data) {
        goal.value = res.data;
        corrupted.value = !!res.message && res.message.includes('损坏');
        if (!goal.value.title && !goal.value.description && total.value === 0) {
          if (panel.value === 'collapsed') { /* keep */ }
        }
        if (!goal.value.title && !goal.value.description && (goal.value.milestones || []).length === 0) {
          if (panel.value !== 'expanded') panel.value = 'mini';
        } else if (panel.value === 'mini') {
          panel.value = 'collapsed';
        }
      }
    } finally { loading.value = false; }
  }

  async function save(ws: string, sess: string, draft: SessionGoalDto) {
    const bridge = getHostBridge();
    const res = await bridge.saveGoal(ws, sess, JSON.stringify(draft));
    if (res.ok && res.data) {
      goal.value = res.data;
      corrupted.value = false;
      return { ok: true as const };
    }
    return { ok: false as const, message: res.message || '保存失败' };
  }

  async function clear(ws: string, sess: string) {
    const bridge = getHostBridge();
    const res = await bridge.clearGoal(ws, sess);
    if (res.ok) {
      goal.value = { title: '', description: '', milestones: [], percent: 0, note: '', updatedAtUtc: new Date().toISOString() };
      corrupted.value = false;
      panel.value = 'mini';
      return { ok: true as const };
    }
    return { ok: false as const, message: res.message || '清空失败' };
  }

  function applyEvent(payload: { sessionId: string; goal: SessionGoalDto }) {
    const ws = useWorkspaceStore();
    const active = ws.activeSessionId || '';
    if (payload && payload.sessionId === active && payload.goal) {
      goal.value = payload.goal;
      corrupted.value = false;
      updatedFlash.value = true;
      setTimeout(() => { updatedFlash.value = false; }, 800);
    }
  }

  function watchSession() {
    const ws = useWorkspaceStore();
    watch(() => [ws.activeWorkspaceId, ws.activeSessionId] as const, ([nws, nsess]) => {
      if (nws && nsess) void load(nws, nsess);
    }, { immediate: true });
    haloEventBus.on('goal:updated', (p: unknown) => {
      try { applyEvent(p as { sessionId: string; goal: SessionGoalDto }); } catch { /* ignore */ }
    });
  }

  return { goal, loading, corrupted, panel, pos, pct, done, total, updatedFlash, setPanel, movePos, load, save, clear, applyEvent, watchSession };
});
