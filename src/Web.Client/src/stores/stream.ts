// src/Web.Client/src/stores/stream.ts
// P5 扩展：notice / retrying / resync / compaction / round / usage。
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

export const useStreamStore = defineStore('stream', () => {
  const busy = ref(false);
  const currentTurnId = ref<string | null>(null);
  const streamingMessageId = ref<string | null>(null);
  const activeTools = ref(new Map<string, ActiveTool>());
  const notice = ref<NoticeState | null>(null);
  const retrying = ref<RetryingState | null>(null);
  const resyncRequired = ref(false);
  const compactionStarted = ref(false);
  const round = ref({ used: 0, limit: 200 });
   const usage = ref({ lastPromptTokens: 0, lastCompletionTokens: 0, dropped: false });
  const queueLength = ref(0);

  const activeToolList = computed(() => [...activeTools.value.values()]);
  const hasActiveTools = computed(() => activeTools.value.size > 0);

  function beginTurn(turnId: string, messageId: string): void {
    busy.value = true;
    currentTurnId.value = turnId;
    streamingMessageId.value = messageId;
    activeTools.value.clear();
    retrying.value = null;
  }

  function endTurn(): void {
    busy.value = false;
    currentTurnId.value = null;
    streamingMessageId.value = null;
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
    currentTurnId,
    streamingMessageId,
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
    beginTurn,
    endTurn,
    addTool,
    patchTool,
    removeTool,
  };
});
