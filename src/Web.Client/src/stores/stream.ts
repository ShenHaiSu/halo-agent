// src/Web.Client/src/stores/stream.ts
// D4-8 流式状态 store（事件 → store 的映射见 eventBridge.ts）。
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
}

/**
 * 流式状态 store。
 *
 * ============ 职责边界 ============
 *  它**不存消息内容**（那是 messages store 的职责）。
 *  它只存"当前正在发生什么"：
 *    - 是否 busy
 *    - 当前回合 id
 *    - 正在执行的工具列表
 * ============ 为什么 activeTools 是 Map ============
 *  工具可能并发（虽然 P2 设计是逐个，但 UI 要能容纳）。
 *  Map 的 O(1) 查找比数组 find 好。
 * ============ ============
 */
export const useStreamStore = defineStore('stream', () => {
  const busy = ref(false);
  const currentTurnId = ref<string | null>(null);
  /** 当前正在流式生成的 assistant 消息 id */
  const streamingMessageId = ref<string | null>(null);
  const activeTools = ref(new Map<string, ActiveTool>());

  const activeToolList = computed(() => [...activeTools.value.values()]);
  const hasActiveTools = computed(() => activeTools.value.size > 0);

  function beginTurn(turnId: string, messageId: string): void {
    busy.value = true;
    currentTurnId.value = turnId;
    streamingMessageId.value = messageId;
    activeTools.value.clear();
  }

  function endTurn(): void {
    busy.value = false;
    currentTurnId.value = null;
    streamingMessageId.value = null;
    activeTools.value.clear();
  }

  function addTool(tool: ActiveTool): void {
    activeTools.value.set(tool.toolCallId, tool);
  }

  function patchTool(toolCallId: string, patch: Partial<ActiveTool>): void {
    const t = activeTools.value.get(toolCallId);
    if (t) Object.assign(t, patch);
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
    beginTurn,
    endTurn,
    addTool,
    patchTool,
    removeTool,
  };
});
