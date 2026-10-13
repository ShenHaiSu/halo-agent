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

let turnStartMark = 0;

/**
 * Stage 06 Agent 状态机：管理消息流、流式增量、工具卡片状态、取消/中断。
 * 订阅 C# JsEventDispatcher 推送的 `llm:stream-event`（文本/思考增量）
 * 与 `agent:turn-event`（迭代/工具/完成/取消/截断）。
 */
export const useAgentStore = defineStore('agent', () => {
  const workspaceStore = useWorkspaceStore();
  const configStore = useConfigStore();

  const streamingText = ref<string>('');
  const streamingReasoning = ref<string>('');
  const streamingActive = ref<boolean>(false);
  const isGenerating = ref<boolean>(false);
  const toolCards = ref<AgentToolCard[]>([]);
  const telemetry = ref<AgentTelemetry>({
    protocol: 'chat',
    ttftMs: null,
    totalMs: null,
    inputTokens: 0,
    outputTokens: 0,
    cachedTokens: 0,
  });
  const turnNotice = ref<string>('');
  const lastError = ref<string>('');

  let listenersBound = false;

  const activeKey = computed(() => {
    const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
    const sess = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
    return `${ws}::${sess}`;
  });

  const hasStreamingContent = computed(() => {
    return streamingActive.value || streamingText.value.length > 0 || streamingReasoning.value.length > 0;
  });

  function resetTurnState(): void {
    streamingText.value = '';
    streamingReasoning.value = '';
    streamingActive.value = false;
    toolCards.value = [];
    telemetry.value = {
      protocol: configStore.activeModel?.protocol || 'chat',
      ttftMs: null,
      totalMs: null,
      inputTokens: 0,
      outputTokens: 0,
      cachedTokens: 0,
    };
    turnNotice.value = '';
    lastError.value = '';
    turnStartMark = Date.now();
  }

  function ensureListeners(): void {
    if (listenersBound) return;
    listenersBound = true;

    haloEventBus.on<SessionStreamEventDto>('llm:stream-event', (evt) => {
      if (!evt || typeof evt.kind !== 'string') return;
      const currentSessionId = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
      if (evt.sessionId && currentSessionId && evt.sessionId !== currentSessionId) return;

      if (evt.kind === 'TextDelta' && evt.textDelta) {
        streamingActive.value = true;
        isGenerating.value = true;
        streamingText.value += evt.textDelta;
      } else if (evt.kind === 'ReasoningDelta' && evt.textDelta) {
        streamingActive.value = true;
        isGenerating.value = true;
        streamingReasoning.value += evt.textDelta;
      } else if (evt.kind === 'UsageUpdated' && evt.usage) {
        telemetry.value.inputTokens = evt.usage.inputTokens || 0;
        telemetry.value.outputTokens = evt.usage.outputTokens || 0;
        telemetry.value.cachedTokens = evt.usage.cachedInputTokens || 0;
      }
    });

    haloEventBus.on<TurnEventPayload>('agent:turn-event', (evt) => {
      if (!evt || typeof evt.kind !== 'string') return;
      const currentSessionId = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
      if (evt.sessionId && currentSessionId && evt.sessionId !== currentSessionId) return;
      handleTurnEvent(evt.kind, evt.payload || {});
    });
  }

  function handleTurnEvent(kind: string, payload: Record<string, unknown>): void {
    const num = (v: unknown): number => (typeof v === 'number' ? v : Number(v) || 0);
    const str = (v: unknown): string => (v === null || v === undefined ? '' : String(v));

    switch (kind) {
      case 'IterationStarted':
        streamingActive.value = true;
        isGenerating.value = true;
        break;
      case 'FirstToken':
        telemetry.value.ttftMs = num(payload['ttftMs']);
        break;
      case 'ToolCallStarted': {
        const callId = str(payload['callId']);
        const functionName = str(payload['functionName']) || 'tool';
        if (callId && !toolCards.value.some((c) => c.callId === callId)) {
          toolCards.value.push({
            callId,
            functionName,
            argsJson: '',
            status: 'running',
            resultText: '',
            exitCode: null,
            durationMs: null,
            iteration: num(payload['iteration']),
          });
        }
        break;
      }
      case 'ToolExecutionStarted': {
        const callId = str(payload['callId']);
        const card = toolCards.value.find((c) => c.callId === callId);
        if (card) {
          card.status = 'running';
          if (payload['functionName']) card.functionName = str(payload['functionName']);
        } else if (callId) {
          toolCards.value.push({
            callId,
            functionName: str(payload['functionName']) || 'tool',
            argsJson: '',
            status: 'running',
            resultText: '',
            exitCode: null,
            durationMs: null,
            iteration: num(payload['iteration']),
          });
        }
        break;
      }
      case 'ToolExecutionCompleted': {
        const callId = str(payload['callId']);
        const card = toolCards.value.find((c) => c.callId === callId);
        if (card) {
          const success = payload['success'] !== false;
          card.status = success ? 'done' : 'error';
          card.exitCode = payload['exitCode'] === null || payload['exitCode'] === undefined ? null : num(payload['exitCode']);
          card.durationMs = payload['durationMs'] === null || payload['durationMs'] === undefined ? null : num(payload['durationMs']);
        }
        void refreshAfterTool();
        break;
      }
      case 'AssistantMessagePersisted': {
        telemetry.value.inputTokens = num(payload['inputTokens']);
        telemetry.value.outputTokens = num(payload['outputTokens']);
        telemetry.value.cachedTokens = num(payload['cachedTokens']);
        void refreshAfterTool();
        break;
      }
      case 'TurnCompleted': {
        telemetry.value.protocol = str(payload['protocol']) || telemetry.value.protocol;
        telemetry.value.inputTokens = num(payload['inputTokens']);
        telemetry.value.outputTokens = num(payload['outputTokens']);
        telemetry.value.cachedTokens = num(payload['cachedTokens']);
        telemetry.value.totalMs = turnStartMark > 0 ? Date.now() - turnStartMark : null;
        isGenerating.value = false;
        streamingActive.value = false;
        void finishTurn();
        break;
      }
      case 'TurnTruncated': {
        telemetry.value.totalMs = turnStartMark > 0 ? Date.now() - turnStartMark : null;
        isGenerating.value = false;
        streamingActive.value = false;
        turnNotice.value = '已达到单轮最大迭代次数（25），已暂停自动执行。请检查中间结果后发送“继续”以恢复任务。';
        void finishTurn();
        break;
      }
      case 'TurnCancelled': {
        telemetry.value.totalMs = turnStartMark > 0 ? Date.now() - turnStartMark : null;
        isGenerating.value = false;
        streamingActive.value = false;
        turnNotice.value = '已停止生成。';
        void finishTurn();
        break;
      }
      case 'TurnError': {
        telemetry.value.totalMs = turnStartMark > 0 ? Date.now() - turnStartMark : null;
        isGenerating.value = false;
        streamingActive.value = false;
        lastError.value = str(payload['errorMessage']) || str(payload['errorCode']) || '推理失败';
        void finishTurn();
        break;
      }
      default:
        break;
    }
  }

  async function refreshAfterTool(): Promise<void> {
    try {
      const sessId = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
      if (sessId) {
        await workspaceStore.loadSessionMessages(sessId);
        await workspaceStore.refreshBudget();
      }
    } catch (err) {
      console.error('[agentStore] refreshAfterTool failed:', err);
    }
  }

  async function finishTurn(): Promise<void> {
    await refreshAfterTool();
    streamingText.value = '';
    streamingReasoning.value = '';
    streamingActive.value = false;
  }

  async function sendPrompt(text: string): Promise<boolean> {
    const content = (text || '').trim();
    if (!content || isGenerating.value) return false;

    const workspaceId = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
    const sessionId = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
    if (!workspaceId || !sessionId) return false;

    ensureListeners();
    resetTurnState();
    isGenerating.value = true;
    streamingActive.value = true;

    try {
      const bridge = getHostBridge();
      const providerId = configStore.activeProvider?.id || '';
      const modelId = configStore.activeModel?.id || '';
      const res = await bridge.startAgentTurn(workspaceId, sessionId, content, providerId, modelId);
      if (!res.ok) {
        lastError.value = res.message || '启动 Agent 推理失败';
        isGenerating.value = false;
        streamingActive.value = false;
        return false;
      }
      // 乐观回显用户消息，等待 TurnCompleted 后与 jsonl 对账
      await workspaceStore.loadSessionMessages(sessionId);
      return true;
    } catch (err) {
      console.error('[agentStore] sendPrompt failed:', err);
      lastError.value = err instanceof Error ? err.message : String(err);
      isGenerating.value = false;
      streamingActive.value = false;
      return false;
    }
  }

  async function stopGenerating(): Promise<void> {
    const sessionId = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
    if (!sessionId) return;
    try {
      const bridge = getHostBridge();
      await bridge.cancelAgentTurn(sessionId);
    } catch (err) {
      console.error('[agentStore] stopGenerating failed:', err);
    } finally {
      isGenerating.value = false;
      streamingActive.value = false;
      turnNotice.value = '已停止生成。';
      await refreshAfterTool();
    }
  }

  function bind(): void {
    ensureListeners();
  }

  return {
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
