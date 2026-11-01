// src/Web.Client/src/stores/stream.ts
// P15：streamingMessageId 删除，改 currentTurn{turnId,activeOrd} + mismatch。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';

export interface ActiveTool {
  toolCallId: string;
  messageId: string;
  name: string;
  arguments: string;
  startedAt: number;
  output: string;
  alertLevel: number;
  kind: 'function' | 'unknown';
}

export interface NoticeState {
  level: 'info' | 'success' | 'warn' | 'error';
  text: string;
}
export interface RetryingState {
  attempt: number;
  max: number;
  reason: string;
}

export interface CurrentTurn {
  turnId: string;
  activeOrd: number;
}

export const useStreamStore = defineStore('stream', () => {
  const busy = ref(false);
  const currentTurn = ref<CurrentTurn | null>(null);
  const activeTools = ref(new Map<string, ActiveTool>());
  const notice = ref<NoticeState | null>(null);
  const retrying = ref<RetryingState | null>(null);
  const resyncRequired = ref(false);
  const compactionStarted = ref(false);
  const round = ref({ used: 0, limit: 200 });
  const usage = ref({ lastPromptTokens: 0, lastCompletionTokens: 0, dropped: false });
  const queueLength = ref(0);
  const mismatch = ref(0);

  const activeToolList = computed(() => [...activeTools.value.values()]);
  const hasActiveTools = computed(() => activeTools.value.size > 0);

  function beginTurn(turnId: string): void {
    busy.value = true;
    currentTurn.value = { turnId, activeOrd: 1 };
    activeTools.value.clear();
    retrying.value = null;
  }

  function setActiveOrd(ord: number): void {
    if (currentTurn.value) currentTurn.value.activeOrd = ord;
  }

  function endTurn(): void {
    busy.value = false;
    currentTurn.value = null;
    activeTools.value.clear();
    retrying.value = null;
  }

  function addTool(tool: ActiveTool): void {
    activeTools.value.set(tool.toolCallId, tool);
  }

  function patchTool(toolCallId: string, p: Partial<ActiveTool>): void {
    const t = activeTools.value.get(toolCallId);
    if (t) Object.assign(t, p);
  }

  function removeTool(toolCallId: string): void {
    activeTools.value.delete(toolCallId);
  }

  return {
    busy,
    currentTurn,
    activeTools,
    activeToolList,
    hasActiveTools,
    notice,
    retrying,
    resyncRequired,
    compactionStarted,
    round,
    usage,
    queueLength,
    mismatch,
    beginTurn,
    setActiveOrd,
    endTurn,
    addTool,
    patchTool,
    removeTool,
  };
});
