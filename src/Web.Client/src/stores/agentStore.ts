import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  getHostBridge,
  haloEventBus,
  type SessionStreamEventDto,
} from '@/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useConfigStore } from '@/stores/configStore';

export type AgentToolCardStatus = 'running' | 'done' | 'error';

export interface AgentToolCard {
  callId: string;
  functionName: string;
  argsJson: string;
  status: AgentToolCardStatus;
  resultText: string;
  exitCode: number | null;
  durationMs: number | null;
  iteration: number;
}

export interface AgentTelemetry {
  protocol: string;
  ttftMs: number | null;
  totalMs: number | null;
  inputTokens: number;
  outputTokens: number;
  cachedTokens: number;
}

interface TurnEventPayload {
  sessionId: string;
  kind: string;
  payload?: Record<string, unknown> | null;
  timestamp?: number;
}

/** 单会话瞬态（TurnStore 统一模型：持久态归 workspaceStore，瞬态归这里） */
export interface AgentTurnState {
  sessionId: string;
  streamingText: string;
  streamingReasoning: string;
  streamingActive: boolean;
  isGenerating: boolean;
  toolCards: AgentToolCard[];
  telemetry: AgentTelemetry;
  turnNotice: string;
  lastError: string;
  updatedAt: number;
}

let turnStartMarks: Record<string, number> = {};

function emptyTelemetry(fallbackProtocol = 'chat'): AgentTelemetry {
  return {
    protocol: fallbackProtocol,
    ttftMs: null,
    totalMs: null,
    inputTokens: 0,
    outputTokens: 0,
    cachedTokens: 0,
  };
}

function emptyTurnState(sessionId: string, fallbackProtocol = 'chat'): AgentTurnState {
  return {
    sessionId,
    streamingText: '',
    streamingReasoning: '',
    streamingActive: false,
    isGenerating: false,
    toolCards: [],
    telemetry: emptyTelemetry(fallbackProtocol),
    turnNotice: '',
    lastError: '',
    updatedAt: Date.now(),
  };
}

function truncateText(s: string, max = 8000): string {
  if (!s) return '';
  return s.length > max ? s.slice(0, max) + '\n...（输出过长已截断）' : s;
}

/**
 * 统一 TurnStore（P0+P1）：
 * - 按 sessionId 分桶，杜绝 A 会话工具卡/错误串显到 B。
 * - 保留原 useAgentStore 导出名与字段别名，模板零改动即可按当前会话隔离显示。
 * - 后台会话事件按 evt.sessionId 落桶，不再按 currentSessionId 丢弃；切回即见进度。
 */
export const useAgentStore = defineStore('agent', () => {
  const workspaceStore = useWorkspaceStore();
  const configStore = useConfigStore();

  const turns = ref<Record<string, AgentTurnState>>({});

  let listenersBound = false;

  const activeSessionId = computed(() => {
    return workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
  });

  const activeKey = computed(() => {
    const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
    return `${ws}::${activeSessionId.value}`;
  });

  function fallbackProtocol(): string {
    return configStore.activeModel?.protocol || 'chat';
  }

  function ensureTurn(sessionId: string): AgentTurnState {
    const sid = (sessionId || '').trim();
    if (!sid) {
      // 无会话时返回一个不落盘的空壳，避免模板空指针；调用方不应写入它
      return emptyTurnState('');
    }
    let t = turns.value[sid];
    if (!t) {
      t = emptyTurnState(sid, fallbackProtocol());
      turns.value[sid] = t;
    }
    return t;
  }

  function getTurn(sessionId: string): AgentTurnState | null {
    const sid = (sessionId || '').trim();
    if (!sid) return null;
    return turns.value[sid] || null;
  }

  const activeTurn = computed<AgentTurnState>(() => ensureTurn(activeSessionId.value));

  // ----  legacy 别名（模板/旧代码继续用，实际读写当前会话那桶） ----
  const streamingText = computed<string>({
    get: () => activeTurn.value.streamingText,
    set: (v) => { activeTurn.value.streamingText = v; },
  });
  const streamingReasoning = computed<string>({
    get: () => activeTurn.value.streamingReasoning,
    set: (v) => { activeTurn.value.streamingReasoning = v; },
  });
  const streamingActive = computed<boolean>({
    get: () => activeTurn.value.streamingActive,
    set: (v) => { activeTurn.value.streamingActive = v; },
  });
  const isGenerating = computed<boolean>({
    get: () => activeTurn.value.isGenerating,
    set: (v) => { activeTurn.value.isGenerating = v; },
  });
  const toolCards = computed<AgentToolCard[]>({
    get: () => activeTurn.value.toolCards,
    set: (v) => { activeTurn.value.toolCards = v; },
  });
  const telemetry = computed<AgentTelemetry>({
    get: () => activeTurn.value.telemetry,
    set: (v) => { activeTurn.value.telemetry = v; },
  });
  const turnNotice = computed<string>({
    get: () => activeTurn.value.turnNotice,
    set: (v) => { activeTurn.value.turnNotice = v; },
  });
  const lastError = computed<string>({
    get: () => activeTurn.value.lastError,
    set: (v) => { activeTurn.value.lastError = v; },
  });

  const hasStreamingContent = computed(() => {
    const t = activeTurn.value;
    return t.streamingActive || t.streamingText.length > 0 || t.streamingReasoning.length > 0;
  });

  const runningSessionIds = computed<string[]>(() =>
    Object.values(turns.value)
      .filter((t) => t.isGenerating || t.streamingActive)
      .map((t) => t.sessionId)
  );

  const isAnyGenerating = computed(() => runningSessionIds.value.length > 0);

  function touch(t: AgentTurnState): void {
    t.updatedAt = Date.now();
  }

  function resetTurnState(sessionId?: string): void {
    const sid = (sessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    turns.value[sid] = emptyTurnState(sid, fallbackProtocol());
    turnStartMarks[sid] = Date.now();
  }

  function clearTurn(sessionId: string): void {
    const sid = (sessionId || '').trim();
    if (!sid) return;
    delete turns.value[sid];
    delete turnStartMarks[sid];
  }

  function resolveTargetSession(evtSessionId: string | undefined | null): string {
    const s = (evtSessionId || '').trim();
    if (s) return s;
    return activeSessionId.value;
  }

  function ensureListeners(): void {
    if (listenersBound) return;
    listenersBound = true;

    haloEventBus.on<SessionStreamEventDto>('llm:stream-event', (evt) => {
      if (!evt || typeof evt.kind !== 'string') return;
      const sid = resolveTargetSession(evt.sessionId);
      if (!sid) return;
      const t = ensureTurn(sid);

      if (evt.kind === 'TextDelta' && evt.textDelta) {
        t.streamingActive = true;
        t.isGenerating = true;
        t.streamingText += evt.textDelta;
        touch(t);
      } else if (evt.kind === 'ReasoningDelta' && evt.textDelta) {
        t.streamingActive = true;
        t.isGenerating = true;
        t.streamingReasoning += evt.textDelta;
        touch(t);
      } else if (evt.kind === 'UsageUpdated' && evt.usage) {
        t.telemetry.inputTokens = evt.usage.inputTokens || 0;
        t.telemetry.outputTokens = evt.usage.outputTokens || 0;
        t.telemetry.cachedTokens = evt.usage.cachedInputTokens || 0;
        touch(t);
      }
    });

    haloEventBus.on<TurnEventPayload>('agent:turn-event', (evt) => {
      if (!evt || typeof evt.kind !== 'string') return;
      const sid = resolveTargetSession(evt.sessionId);
      if (!sid) return;
      handleTurnEvent(evt.kind, evt.payload || {}, sid);
    });
  }

  function pickArgs(payload: Record<string, unknown>): string {
    const cands = [payload['argsJson'], payload['argumentsJson'], payload['arguments']];
    for (const c of cands) {
      if (typeof c === 'string' && c) return c;
    }
    return '';
  }

  function handleTurnEvent(kind: string, payload: Record<string, unknown>, sessionId?: string): void {
    const sid = (sessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    const t = ensureTurn(sid);
    const num = (v: unknown): number => (typeof v === 'number' ? v : Number(v) || 0);
    const str = (v: unknown): string => (v === null || v === undefined ? '' : String(v));

    switch (kind) {
      case 'IterationStarted':
        t.streamingActive = true;
        t.isGenerating = true;
        touch(t);
        break;
      case 'FirstToken':
        t.telemetry.ttftMs = num(payload['ttftMs']);
        touch(t);
        break;
      case 'ToolCallStarted': {
        const callId = str(payload['callId']);
        const functionName = str(payload['functionName']) || 'tool';
        const args = pickArgs(payload);
        if (callId && !t.toolCards.some((c) => c.callId === callId)) {
          t.toolCards.push({
            callId,
            functionName,
            argsJson: args,
            status: 'running',
            resultText: '',
            exitCode: null,
            durationMs: null,
            iteration: num(payload['iteration']),
          });
        } else if (callId && args) {
          const ex = t.toolCards.find((c) => c.callId === callId);
          if (ex && !ex.argsJson) ex.functionName = functionName || ex.functionName;
          if (ex && !ex.argsJson) ex.argsJson = args;
        }
        touch(t);
        break;
      }
      case 'ToolCallUpdated':
      case 'ToolCallArgs':
      case 'ToolCallCompleted': {
        const callId = str(payload['callId']);
        const args = pickArgs(payload);
        const card = t.toolCards.find((c) => c.callId === callId);
        if (card && args) {
          card.argsJson = args;
          if (payload['functionName']) card.functionName = str(payload['functionName']);
        } else if (callId && args) {
          t.toolCards.push({
            callId,
            functionName: str(payload['functionName']) || 'tool',
            argsJson: args,
            status: 'running',
            resultText: '',
            exitCode: null,
            durationMs: null,
            iteration: num(payload['iteration']),
          });
        }
        touch(t);
        break;
      }
      case 'ToolExecutionStarted': {
        const callId = str(payload['callId']);
        const args = pickArgs(payload);
        const card = t.toolCards.find((c) => c.callId === callId);
        if (card) {
          card.status = 'running';
          if (payload['functionName']) card.functionName = str(payload['functionName']);
          if (args) card.argsJson = args;
        } else if (callId) {
          t.toolCards.push({
            callId,
            functionName: str(payload['functionName']) || 'tool',
            argsJson: args,
            status: 'running',
            resultText: '',
            exitCode: null,
            durationMs: null,
            iteration: num(payload['iteration']),
          });
        }
        touch(t);
        break;
      }
      case 'ToolExecutionCompleted': {
        const callId = str(payload['callId']);
        const card = t.toolCards.find((c) => c.callId === callId);
        if (card) {
          const success = payload['success'] !== false;
          card.status = success ? 'done' : 'error';
          card.exitCode = payload['exitCode'] === null || payload['exitCode'] === undefined ? null : num(payload['exitCode']);
          card.durationMs = payload['durationMs'] === null || payload['durationMs'] === undefined ? null : num(payload['durationMs']);
          const args = pickArgs(payload);
          if (args) card.argsJson = args;
          const rt = payload['resultText'];
          if (typeof rt === 'string' && rt) card.resultText = truncateText(rt);
        }
        touch(t);
        void refreshAfterTool(sid);
        break;
      }
      case 'AssistantMessagePersisted': {
        t.telemetry.inputTokens = num(payload['inputTokens']);
        t.telemetry.outputTokens = num(payload['outputTokens']);
        t.telemetry.cachedTokens = num(payload['cachedTokens']);
        touch(t);
        void refreshAfterTool(sid);
        break;
      }
      case 'TurnCompleted': {
        t.telemetry.protocol = str(payload['protocol']) || t.telemetry.protocol;
        t.telemetry.inputTokens = num(payload['inputTokens']);
        t.telemetry.outputTokens = num(payload['outputTokens']);
        t.telemetry.cachedTokens = num(payload['cachedTokens']);
        t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
        t.isGenerating = false;
        t.streamingActive = false;
        touch(t);
        void finishTurn(sid);
        void workspaceStore.generateTitleAfterFirstRound(sid).catch(() => undefined);
        break;
      }
      case 'TurnTruncated': {
        t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
        t.isGenerating = false;
        t.streamingActive = false;
        t.turnNotice = '已达到单轮最大迭代次数（25），已暂停自动执行。请检查中间结果后发送“继续”以恢复任务。';
        touch(t);
        void finishTurn(sid);
        void workspaceStore.generateTitleAfterFirstRound(sid).catch(() => undefined);
        break;
      }
      case 'TurnCancelled': {
        t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
        t.isGenerating = false;
        t.streamingActive = false;
        t.turnNotice = '已停止生成。';
        touch(t);
        void finishTurn(sid);
        break;
      }
      case 'TurnError': {
        t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
        t.isGenerating = false;
        t.streamingActive = false;
        t.lastError = str(payload['errorMessage']) || str(payload['errorCode']) || '推理失败';
        touch(t);
        void finishTurn(sid);
        void workspaceStore.generateTitleAfterFirstRound(sid).catch(() => undefined);
        break;
      }
      default:
        break;
    }
  }

  async function refreshAfterTool(targetSessionId?: string): Promise<void> {
    try {
      const sid = (targetSessionId ?? activeSessionId.value).trim();
      if (!sid) return;
      // 后台会话只落桶、不碰当前视图：避免把 B 的消息列表刷成 A 的
      if (sid !== activeSessionId.value) return;
      await workspaceStore.loadSessionMessages(sid);
      await workspaceStore.refreshBudget();
    } catch (err) {
      console.error('[agentStore] refreshAfterTool failed:', err);
    }
  }

  async function finishTurn(targetSessionId?: string): Promise<void> {
    const sid = (targetSessionId ?? activeSessionId.value).trim();
    if (sid) {
      await refreshAfterTool(sid);
      const t = ensureTurn(sid);
      // 只清流式增量，保留 toolCards/notice/error 供切回回看；下一轮 sendPrompt 会清该桶
      t.streamingText = '';
      t.streamingReasoning = '';
      t.streamingActive = false;
      touch(t);
    }
  }

  async function sendPrompt(text: string): Promise<boolean> {
    const content = (text || '').trim();
    const sessionId = activeSessionId.value;
    if (!content || !sessionId) return false;
    if (ensureTurn(sessionId).isGenerating) return false;

    const workspaceId = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
    if (!workspaceId || !sessionId) return false;

    ensureListeners();
    resetTurnState(sessionId);
    const t = ensureTurn(sessionId);
    t.isGenerating = true;
    t.streamingActive = true;
    touch(t);

    try {
      const bridge = getHostBridge();
      const providerId = configStore.activeProvider?.id || '';
      const modelId = configStore.activeModel?.id || '';
      const res = await bridge.startAgentTurn(workspaceId, sessionId, content, providerId, modelId);
      if (!res.ok) {
        t.lastError = res.message || '启动 Agent 推理失败';
        t.isGenerating = false;
        t.streamingActive = false;
        touch(t);
        return false;
      }
      // 乐观回显用户消息，等待 TurnCompleted 后与 jsonl 对账
      await workspaceStore.loadSessionMessages(sessionId);
      return true;
    } catch (err) {
      console.error('[agentStore] sendPrompt failed:', err);
      t.lastError = err instanceof Error ? err.message : String(err);
      t.isGenerating = false;
      t.streamingActive = false;
      touch(t);
      return false;
    }
  }

  async function stopGenerating(targetSessionId?: string): Promise<void> {
    const sessionId = (targetSessionId ?? activeSessionId.value).trim();
    if (!sessionId) return;
    try {
      const bridge = getHostBridge();
      await bridge.cancelAgentTurn(sessionId);
    } catch (err) {
      console.error('[agentStore] stopGenerating failed:', err);
    } finally {
      const t = ensureTurn(sessionId);
      t.isGenerating = false;
      t.streamingActive = false;
      t.turnNotice = '已停止生成。';
      touch(t);
      await refreshAfterTool(sessionId);
    }
  }

  function bind(): void {
    ensureListeners();
  }

  // ---- 预留接口（P2 直接复用，当前为最小可用实现） ----
  function subscribe(sessionId: string, cb: (s: AgentTurnState) => void): () => void {
    void sessionId;
    void cb;
    // 简单实现：调用方可用 watch(activeTurn) 代替；保留签名供分屏/后台 badge 订阅
    return () => undefined;
  }

  function queryActiveTurns(): string[] {
    return runningSessionIds.value;
  }

  function pauseTurn(_sessionId: string): void {
    throw new Error('pauseTurn not-supported yet');
  }

  function resumeTurn(_sessionId: string): void {
    throw new Error('resumeTurn not-supported yet');
  }

  return {
    // 新统一接口
    turns,
    activeTurn,
    activeSessionId,
    runningSessionIds,
    isAnyGenerating,
    ensureTurn,
    getTurn,
    clearTurn,
    queryActiveTurns,
    subscribe,
    pauseTurn,
    resumeTurn,
    // 兼容旧模板/旧调用
    streamingText,
    streamingReasoning,
    streamingActive,
    isGenerating,
    toolCards,
    telemetry,
    turnNotice,
    lastError,
    activeKey,
    hasStreamingContent,
    bind,
    resetTurnState,
    sendPrompt,
    stopGenerating,
  };
});
