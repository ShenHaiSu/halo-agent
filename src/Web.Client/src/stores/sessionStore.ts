import { defineStore } from 'pinia';
import { ref, computed } from 'vue';

/**
 * need22-P2 F1+F3：会话单一真相源。
 * - activeSessionId 唯一写点：workspaceStore.select/load/create/delete 写入，其余只读。
 * - sessionEpoch：每次 selectSession 递增；旧 epoch 的异步响应必须丢弃。
 * - 每个 epoch 配一个 AbortController；select 时 abort 上一个。
 * - staleDiscarded：旧 epoch 丢弃计数（console.debug 可观测）。
 */
export const useSessionStore = defineStore('session', () => {
  const activeSessionId = ref<string>('');
  const epoch = ref(0);
  const staleDiscarded = ref(0);
  let controller: AbortController | null = null;

  const currentSignal = computed<AbortSignal | null>(() => controller?.signal ?? null);

  /** 切会话时调用：递增 epoch 并换发 AbortController，返回本次 epoch 与 signal。 */
  function beginSelect(sessionId: string): { epoch: number; signal: AbortSignal } {
    if (controller) {
      try { controller.abort(); } catch { /* ignore */ }
    }
    controller = new AbortController();
    epoch.value += 1;
    activeSessionId.value = (sessionId || '').trim();
    return { epoch: epoch.value, signal: controller.signal };
  }

  /** 同步外部已确定的 activeSession（loadSessions 首选恢复、create/delete），不换发 controller。 */
  function syncActive(sessionId: string): void {
    activeSessionId.value = (sessionId || '').trim();
  }

  function isCurrent(checkEpoch: number, signal?: AbortSignal | null): boolean {
    if (checkEpoch !== epoch.value) return false;
    if (signal && signal.aborted) return false;
    if (controller && signal && controller.signal !== signal) return false;
    return true;
  }

  function discardStale(reason: string): void {
    staleDiscarded.value += 1;
    console.debug(`[sessionStore] stale response discarded (${reason}), total=${staleDiscarded.value}`);
  }

  return {
    activeSessionId,
    epoch,
    staleDiscarded,
    currentSignal,
    beginSelect,
    syncActive,
    isCurrent,
    discardStale,
  };
});
