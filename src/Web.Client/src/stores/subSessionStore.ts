import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import { getHostBridge, haloEventBus, type SessionMetadata, type SubSessionDetailDto, type UnifiedChatMessage } from '@/bridge/hostBridge';

/** need22-P3 G4：事件驱动（后端推 sub-event，复用 P1 事件通道 haloEventBus），轮询仅降级。 */
export const SUB_POLL_FALLBACK_MS = 15000;

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
  const eventDriven = ref(false);
  let pollTimer: number | null = null;
  let failCount = 0;
  let unsubSubEvent: (() => void) | null = null;
  let lastWs = '';

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
    } catch (e) {
      console.warn('[sub-session] refreshList failed:', e);
    }
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
      console.warn('[sub-session] refreshMessages failed:', e);
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
    } catch (e) {
      console.warn('[sub-session] refreshDetail failed:', e);
    }
  }

  /** 后端 sub-event：只处理本 activeSub 的增量，实时性 <1s；事件 5s 无心跳才启用轮询降级 */
   function startEventDriven(ws: string) {
     stopEventDriven();
     lastWs = ws;
     let eventSeen = false;
     const onSubEvent = (evt: unknown) => {
       try {
         // haloEventBus.on 回调收到的即 payload 本体（非 envelope）
         const p = (evt ?? {}) as Record<string, unknown>;
         const subId = String(p['subId'] ?? p['subSessionId'] ?? '');
         if (subId && subId !== activeSubId.value) return;
         eventSeen = true;
         eventDriven.value = true;
         // 增量优先：带消息直接合入，否则拉一次对账
         const inc = p['message'] as UnifiedChatMessage | undefined;
         if (inc && typeof inc === 'object' && inc.id) {
           if (!messages.value.some((m) => String(m.id) === String(inc.id))) messages.value = [...messages.value, inc];
         } else {
           void refreshMessages(ws);
         }
         if (p['status']) void refreshDetail(ws);
       } catch (e) {
         console.warn('[sub-session] sub-event handle failed:', e);
       }
     };
     try {
       const off1 = haloEventBus.on('agent:sub-event', onSubEvent);
       const off2 = haloEventBus.on('sub-session-updated', onSubEvent);
       unsubSubEvent = () => {
         try { off1(); } catch (e) { console.warn('[sub-session] unsub failed:', e); }
         try { off2(); } catch (e) { console.warn('[sub-session] unsub failed:', e); }
       };
     } catch (e) {
       console.warn('[sub-session] event subscribe failed, fallback to poll:', e);
     }
    // 降级：5s 内无事件才开 15s 轮询
    window.setTimeout(() => {
      if (!eventSeen && activeSubId.value) startPoll(ws);
    }, 5000);
    document.addEventListener('visibilitychange', onVisibility);
  }

  function stopEventDriven() {
    try { unsubSubEvent?.(); } catch (e) { console.warn('[sub-session] unsub failed:', e); }
    unsubSubEvent = null;
    eventDriven.value = false;
    try { document.removeEventListener('visibilitychange', onVisibility); } catch (e) { console.warn('[sub-session] rm listener failed:', e); }
  }

  function onVisibility() {
    // document.hidden 时暂停轮询，可见恢复
    if (document.hidden) {
      if (pollTimer !== null) { clearInterval(pollTimer); pollTimer = null; }
    } else if (polling.value && lastWs && !pollTimer) {
      startPollTimer(lastWs);
    }
  }

  function startPollTimer(ws: string) {
    if (pollTimer !== null) clearInterval(pollTimer);
    pollTimer = window.setInterval(() => {
      if (document.hidden) return;
      if (detail.value?.status === 'running' || detail.value?.status === 'pending') {
        void refreshMessages(ws);
        void refreshDetail(ws);
      } else {
        void refreshMessages(ws);
      }
    }, SUB_POLL_FALLBACK_MS);
  }

  function startPoll(ws: string) {
    stopPollTimerOnly();
    polling.value = true;
    failCount = 0;
    lastWs = ws;
    startPollTimer(ws);
  }

  function stopPollTimerOnly() {
    if (pollTimer !== null) { clearInterval(pollTimer); pollTimer = null; }
  }

  function stopPoll() {
    polling.value = false;
    stopPollTimerOnly();
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
      if (detail.value?.status === 'running' || detail.value?.status === 'pending') {
        startEventDriven(ws);
      }
    } finally { loading.value = false; }
  }

  function exitSub() {
    stopPoll();
    stopEventDriven();
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

  return { mode, parentId, activeSubId, subs, detail, messages, loading, error, polling, eventDriven, activeSubLabel, shortId, enterSub, exitSub, refreshList, refreshMessages, refreshDetail, startPoll, stopPoll, cancelSub };
});
