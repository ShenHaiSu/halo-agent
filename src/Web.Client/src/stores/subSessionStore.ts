import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { getHostBridge, type SessionMetadata, type SubSessionDetailDto, type UnifiedChatMessage } from '@/bridge/hostBridge';

export const useSubSessionStore = defineStore('subSession', () => {
  const mode = ref<'main' | 'sub'>('main');
  const parentId = ref('');
  const activeSubId = ref('');
  const subs = ref<SessionMetadata[]>([]);
  const detail = ref<SubSessionDetailDto | null>(null);
  const messages = ref<UnifiedChatMessage[]>([]);
  const loading = ref(false);
  const error = ref('');
  const polling = ref(false);
  let pollTimer: number | null = null;
  let failCount = 0;

  const activeSubLabel = computed(() => {
    const hit = subs.value.find((s) => s.id === activeSubId.value);
    return (hit?.subLabel || hit?.title || '').slice(0, 20);
  });
  const shortId = computed(() => (activeSubId.value || '').slice(-6));

  async function refreshList(ws: string, parent: string) {
    try {
      const bridge = getHostBridge();
      const res = await bridge.getSubSessions(ws, parent);
      if (res.ok && res.data) subs.value = res.data;
    } catch { /* ignore */ }
  }

  async function refreshMessages(ws: string) {
    if (!ws || !activeSubId.value) return;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getSubSessionMessages(ws, activeSubId.value);
      if (res.ok && res.data) {
        messages.value = res.data;
        error.value = '';
        failCount = 0;
      } else {
        error.value = res.message || '加载失败';
        failCount++;
        if (failCount >= 3) stopPoll();
      }
    } catch (e) {
      error.value = String(e);
      failCount++;
      if (failCount >= 3) stopPoll();
    }
  }

  async function refreshDetail(ws: string) {
    if (!ws || !activeSubId.value) return;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getSubSessionDetail(ws, activeSubId.value);
      if (res.ok && res.data) detail.value = res.data;
    } catch { /* ignore */ }
  }

  function startPoll(ws: string) {
    stopPoll();
    polling.value = true;
    failCount = 0;
    pollTimer = window.setInterval(() => {
      if (detail.value?.status === 'running' || detail.value?.status === 'pending') {
        void refreshMessages(ws);
        void refreshDetail(ws);
      } else {
        void refreshMessages(ws);
      }
    }, 3000);
  }

  function stopPoll() {
    polling.value = false;
    if (pollTimer !== null) { clearInterval(pollTimer); pollTimer = null; }
  }

  async function enterSub(ws: string, parent: string, sub: string) {
    mode.value = 'sub';
    parentId.value = parent;
    activeSubId.value = sub;
    loading.value = true;
    error.value = '';
    try {
      await refreshList(ws, parent);
      await refreshDetail(ws);
      await refreshMessages(ws);
      if (detail.value?.status === 'running' || detail.value?.status === 'pending') startPoll(ws);
    } finally { loading.value = false; }
  }

  function exitSub() {
    stopPoll();
    mode.value = 'main';
    activeSubId.value = '';
    detail.value = null;
    messages.value = [];
    error.value = '';
  }

  async function cancelSub(ws: string) {
    if (!activeSubId.value) return { ok: false as const, message: '无子会话' };
    const bridge = getHostBridge();
    const res = await bridge.cancelSubAgent(ws, activeSubId.value);
    if (res.ok) {
      await refreshDetail(ws);
      return { ok: true as const };
    }
    return { ok: false as const, message: res.message || '取消失败' };
  }

  return { mode, parentId, activeSubId, subs, detail, messages, loading, error, polling, activeSubLabel, shortId, enterSub, exitSub, refreshList, refreshMessages, refreshDetail, startPoll, stopPoll, cancelSub };
});
