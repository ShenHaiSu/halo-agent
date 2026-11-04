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
  const cancelling = ref(false);            // ★ P24-D3：已发出取消请求，等引擎收尾
  const cancelRequestedAt = ref(0);         // ★ P24-D5：取消请求时刻（performance.now()），工具帧判定用
  const cancelTimedOut = ref(false);       // ★ 兜底触发过（UI 可据此提示“可能仍在输出”）
  let cancelTimer: number | null = null;    // 静默期兜底定时器

  const activeToolList = computed(() => [...activeTools.value.values()]);
  const hasActiveTools = computed(() => activeTools.value.size > 0);

  function beginTurn(turnId: string): void {
    busy.value = true;
    currentTurn.value = { turnId, activeOrd: 1 };
    activeTools.value.clear();
    retrying.value = null;
    endCancel();                       // ★ P24-O3：新回合开始必须清掉上一轮的取消态
  }

  function setActiveOrd(ord: number): void {
    if (currentTurn.value) currentTurn.value.activeOrd = ord;
  }

  function endTurn(): void {
    busy.value = false;
    currentTurn.value = null;
    activeTools.value.clear();
    retrying.value = null;
    endCancel();                       // ★ P24-D3：收尾即退出取消态
  }

  /**
   * P24-D3：进入「正在中断」态。
   * 为什么放在 store 而不是组件：
   *   ① StopButton / Composer / QueueList 都要读它；
   *   ② 静默期定时器必须跨组件存活（组件可能重渲染）；
   *   ③ beginTurn / endTurn 里统一复位，避免每个入口各写一遍。
   */
  function beginCancel(): void {
    cancelling.value = true;
    cancelRequestedAt.value = performance.now();
    cancelTimedOut.value = false;
    if (cancelTimer !== null) window.clearTimeout(cancelTimer);
    cancelTimer = window.setTimeout(() => {
      cancelTimer = null;
      console.warn('[cancel] 静默期 2000ms 内未收到 turn.finished，本地复位');
      cancelling.value = false;
      cancelTimedOut.value = true;
      // ★ P24-O4：这里不能调 endTurn()。
      //   endTurn 会清 currentTurn，导致随后迟到的 turn.finished 找不到宿主、
      //   ensureTurn() 新建一个孤儿 turn —— 用户会看到“两条回复”。
      //   正确做法：只退出 busy（让输入框可用），保留 currentTurn 等迟到帧。
      busy.value = false;
    }, 2000);
  }

  /** 收到 turn.finished 或 busyChanged{false} 时复位。 */
  function endCancel(): void {
    if (cancelTimer !== null) {
      window.clearTimeout(cancelTimer);
      cancelTimer = null;
    }
    cancelling.value = false;
    cancelTimedOut.value = false;
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
    cancelling,
    cancelRequestedAt,
    cancelTimedOut,
    beginTurn,
    setActiveOrd,
    endTurn,
    beginCancel,
    endCancel,
    addTool,
    patchTool,
    removeTool,
  };
});
