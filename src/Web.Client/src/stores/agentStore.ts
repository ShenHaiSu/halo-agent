import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  getHostBridge,
  haloEventBus,
  type SessionStreamEventDto,
} from '@/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useConfigStore } from '@/stores/configStore';
// need09-06：问询 pending 视为忙（askUserStore 不反向依赖本 store，无循环）
import { useAskUserStore } from '@/stores/askUserStore';

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
    /** need12 R1：流式参数已接收字节（UTF-8，后端节流发布；旧后端回退估算）。 */
    receivedBytes: number;
    /** need12 R1：工具落盘字节（WriteFileTool 完成态回填）。 */
    fileBytes?: number | null;
    images?: Array<{ imageId: string; fileName: string; mime: string; relPath: string; width: number; height: number; sizeBytes: number; sha16: string; lost?: boolean }>;
  }
 
 /** need09-04：sendPrompt 可选第二参附件项 */
 export interface AgentSendAttachment { attId: string; fileName: string; mime: string; base64: string }

 export interface AgentTelemetry {
   protocol: string;
   ttftMs: number | null;
   totalMs: number | null;
   inputTokens: number;
   outputTokens: number;
   cachedTokens: number;
   // need05-01：Σ账单 + 占用态 + 流式估算（与 znaide round_est + ≈ 同语义）
   billedPrompt: number;
   billedCompletion: number;
   occupancyState: string;
   liveEst: number;
 }
 
 /** need05-01：前端估算函数（与 znaide app.rs:2119 同口径）：ASCII/4 向上取整，非 ASCII 按 1 */
 export function estTokens(text: string): number {
   if (!text) return 0;
   let ascii = 0;
   let other = 0;
   for (const ch of text) {
     const cp = ch.codePointAt(0) || 0;
     if (cp <= 127) ascii++;
     else other++;
   }
   return other + Math.ceil(ascii / 4);
 }
 
 /** need12 R1：旧后端兼容——前端按 UTF-8 估算 delta 字节数（与后端 GetByteCount 同单位）。 */
 export function estimateUtf8Bytes(text: string): number {
   if (!text) return 0;
   try {
     if (typeof TextEncoder !== 'undefined') return new TextEncoder().encode(text).length;
   } catch { /* fall through */ }
   let n = 0;
   for (const ch of text) {
     const cp = ch.codePointAt(0) || 0;
     if (cp <= 0x7f) n += 1;
     else if (cp <= 0x7ff) n += 2;
     else if (cp <= 0xffff) n += 3;
     else n += 4;
   }
   return n;
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
  /** need13：主会话子进度（瞬态，不持久化） */
  subAgents: { total: number; done: number; waiting: boolean; items: Array<{ subId: string; label: string; status: string }> };
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
    billedPrompt: 0,
    billedCompletion: 0,
    occupancyState: 'known',
    liveEst: 0,
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
    subAgents: { total: 0, done: 0, waiting: false, items: [] },
  };
}

function truncateText(s: string, max = 6000): string {
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
  // need04-03/04：本轮去重与归并快照
  const dirtyPersisted = ref<Record<string, boolean>>({});
  const lastTurnSnapshot = ref<Record<string, { toolCards: AgentToolCard[]; reasoning: string; text: string; at: number }>>({});

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

  // need04-03：一轮一容器显隐：流式文本/思考/工具卡任一存在即显示
  const hasActiveTurn = computed(() => {
    const t = activeTurn.value;
    return t.streamingActive || t.streamingText.length > 0 || t.streamingReasoning.length > 0 || t.toolCards.length > 0;
  });

  // need04-03：本轮已在流式卡中存在的 callId，持久区 tool 灰块隐藏（只藏显示不删数据）
  const hiddenToolCallIds = computed<Set<string>>(() => new Set(activeTurn.value.toolCards.map((c) => c.callId).filter(Boolean)));
  // need13：主会话子进度别名（模板直接用 activeTurn.subAgents 亦可，此处供 PromptComposer 便捷绑定）
  const subAgents = computed(() => activeTurn.value.subAgents);

  const runningSessionIds = computed<string[]>(() =>
    Object.values(turns.value)
      .filter((t) => t.isGenerating || t.streamingActive)
      .map((t) => t.sessionId)
  );

  const isAnyGenerating = computed(() => runningSessionIds.value.length > 0);

  /** need04-01：任何写消息路径结束时内存态秒级刷新会话卡时间（乐观+事件双通道）。 */
  function touchSessionClock(sessionId: string, iso?: string): void {
    try {
      const ws = useWorkspaceStore();
      const fn = (ws as unknown as { touchSessionTime?: (sid: string, iso?: string) => void }).touchSessionTime;
      if (typeof fn === 'function') fn.call(ws, sessionId, iso);
    } catch { /* 时间刷新永不阻断主链路 */ }
  }

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
        t.telemetry.liveEst += estTokens(evt.textDelta); // need05-01：流式中 ≈ 估算
        touch(t);
      } else if (evt.kind === 'ReasoningDelta' && evt.textDelta) {
        t.streamingActive = true;
        t.isGenerating = true;
        t.streamingReasoning += evt.textDelta;
        t.telemetry.liveEst += estTokens(evt.textDelta);
        touch(t);
      } else if (evt.kind === 'UsageUpdated' && evt.usage) {
        t.telemetry.inputTokens = evt.usage.inputTokens || 0;
        t.telemetry.outputTokens = evt.usage.outputTokens || 0;
        t.telemetry.cachedTokens = evt.usage.cachedInputTokens || 0;
        t.telemetry.liveEst = 0; // need05-01：Usage 到账切真实，≈ 消失
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
         const rb = typeof payload['receivedBytes'] === 'number' ? (payload['receivedBytes'] as number) : 0;
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
             receivedBytes: rb,
           });
         } else if (callId && args) {
           const ex = t.toolCards.find((c) => c.callId === callId);
           if (ex && !ex.argsJson) ex.functionName = functionName || ex.functionName;
           if (ex && !ex.argsJson) ex.argsJson = args;
           if (ex) ex.receivedBytes = rb;
         }
         touch(t);
         break;
       }
       case 'ToolCallUpdated':
       case 'ToolCallArgs':
       case 'ToolCallCompleted': {
         const callId = str(payload['callId']);
         const args = pickArgs(payload);
         const argsDelta = typeof payload['argsDelta'] === 'string' ? (payload['argsDelta'] as string) : '';
         const hasBytes = typeof payload['receivedBytes'] === 'number';
         const fallbackBytes = argsDelta ? estimateUtf8Bytes(argsDelta) : 0;
         const card = t.toolCards.find((c) => c.callId === callId);
         if (card) {
           if (args) {
             card.argsJson = args;
             if (kind === 'ToolCallCompleted') {
               // 终值对账：全量 args 的 UTF-8 长度即终值（覆盖节流欠量）
               card.receivedBytes = estimateUtf8Bytes(args);
             } else if (hasBytes) {
               card.receivedBytes = num(payload['receivedBytes']);
             } else if (fallbackBytes > 0) {
               card.receivedBytes = (card.receivedBytes || 0) + fallbackBytes;
             }
           } else if (hasBytes) {
             card.receivedBytes = num(payload['receivedBytes']);
           } else if (fallbackBytes > 0) {
             card.receivedBytes = (card.receivedBytes || 0) + fallbackBytes;
           }
           if (payload['functionName']) card.functionName = str(payload['functionName']);
         } else if (callId && (args || argsDelta || hasBytes)) {
           t.toolCards.push({
             callId,
             functionName: str(payload['functionName']) || 'tool',
             argsJson: args,
             status: 'running',
             resultText: '',
             exitCode: null,
             durationMs: null,
             iteration: num(payload['iteration']),
             receivedBytes: hasBytes ? num(payload['receivedBytes']) : fallbackBytes,
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
             receivedBytes: 0,
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
           const fb = payload['fileBytes'];
           card.fileBytes = typeof fb === 'number' && Number.isFinite(fb) ? fb : null;
         }
         // need04-03：工具完成只内存更新+标脏，不全量拉取（跳动主因）；等 AssistantMessagePersisted 再对账
         dirtyPersisted.value[sid] = true;
         touch(t);
         touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
         void workspaceStore.refreshBudget().catch(() => undefined);
         break;
       }
      case 'AssistantMessagePersisted': {
        t.telemetry.inputTokens = num(payload['inputTokens']);
        t.telemetry.outputTokens = num(payload['outputTokens']);
        t.telemetry.cachedTokens = num(payload['cachedTokens']);
        t.telemetry.billedPrompt = num(payload['billedPrompt']); // need05-01：Σ
        t.telemetry.billedCompletion = num(payload['billedCompletion']);
        t.telemetry.liveEst = 0;
        touch(t);
        touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
        void refreshAfterTool(sid, 'persisted');
        break;
      }
      case 'TurnCompleted': {
        t.telemetry.protocol = str(payload['protocol']) || t.telemetry.protocol;
        t.telemetry.inputTokens = num(payload['inputTokens']);
        t.telemetry.outputTokens = num(payload['outputTokens']);
        t.telemetry.cachedTokens = num(payload['cachedTokens']);
        t.telemetry.billedPrompt = num(payload['billedPrompt']); // need05-01：Σ
        t.telemetry.billedCompletion = num(payload['billedCompletion']);
        t.telemetry.liveEst = 0;
        t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
        t.isGenerating = false;
        t.streamingActive = false;
        // need13：完成即解除子等待
        t.subAgents.waiting = false;
        t.subAgents.done = t.subAgents.total;
        touch(t);
        touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
        void finishTurn(sid);
        void workspaceStore.generateTitleAfterFirstRound(sid).catch(() => undefined);
        break;
      }
      case 'CompactStarted': {
        // need05-03：压缩开始，不可中断；按钮置灰由 turnNotice 含“压缩中”驱动
        t.isGenerating = true;
        t.turnNotice = '压缩中…不可中断';
        touch(t);
        break;
      }
      case 'CompactCompleted': {
        t.isGenerating = false;
        t.streamingActive = false;
        t.turnNotice = `✔ 已压缩：${num(payload['removedMessages'])}条收敛为摘要`;
        touch(t);
        touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
        void refreshAfterTool(sid, 'finish'); // 重拉消息与预算
        break;
      }
      case 'CompactFailed': {
        t.isGenerating = false;
        t.streamingActive = false;
        t.lastError = str(payload['errorMessage']) || str(payload['errorCode']) || '压缩失败';
        touch(t);
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
        // need13：取消掐断整树，子等待态同步解除
        t.subAgents.waiting = false;
        touch(t);
        void finishTurn(sid);
        break;
      }
      case 'TurnError': {
        t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
        t.isGenerating = false;
        t.streamingActive = false;
        t.lastError = str(payload['errorMessage']) || str(payload['errorCode']) || '推理失败';
        // need13：错误即解除子等待
        t.subAgents.waiting = false;
        touch(t);
        void finishTurn(sid);
        void workspaceStore.generateTitleAfterFirstRound(sid).catch(() => undefined);
        break;
      }
      default:
        break;
    }
    // need13：子代理聚合事件（agent:sub-event 由桥接层转发为 turn-event，kind 保持 SubAgentProgress）
    if (kind === 'SubAgentsStarted' || kind === 'SubAgentProgress' || kind === 'SubAgentsBarrier' || kind === 'SubAgentsBarrierReleased' || kind === 'agent:sub-event') {
      handleSubEvent(sid, kind, payload);
    }
  }

  /** need13：子进度聚合（主桶隔离；subId 事件不污染主时间线，只更新 items） */
  function handleSubEvent(sid: string, kind: string, payload: Record<string, unknown> | null | undefined) {
    const t = ensureTurn(sid);
    const p = payload ?? {};
    if (kind === 'SubAgentsStarted') {
      const subs = Array.isArray(p['subs']) ? (p['subs'] as Array<Record<string, unknown>>) : [];
      t.subAgents.total = typeof p['total'] === 'number' ? (p['total'] as number) : subs.length;
      t.subAgents.waiting = true;
      for (const s of subs) {
        const subId = String(s['subId'] ?? '');
        if (subId && !t.subAgents.items.some((i) => i.subId === subId)) {
          t.subAgents.items.push({ subId, label: String(s['label'] ?? ''), status: 'running' });
        }
      }
      touch(t);
      return;
    }
    if (kind === 'SubAgentProgress' || kind === 'agent:sub-event') {
      const subId = String(p['subId'] ?? '');
      const status = String(p['status'] ?? 'running');
      if (subId) {
        const ex = t.subAgents.items.find((i) => i.subId === subId);
        if (ex) ex.status = status;
        else t.subAgents.items.push({ subId, label: String(p['label'] ?? ''), status });
      }
      t.subAgents.done = t.subAgents.items.filter((i) => i.status === 'succeeded' || i.status === 'failed' || i.status === 'cancelled').length;
      touch(t);
      return;
    }
    if (kind === 'SubAgentsBarrier') {
      t.subAgents.waiting = true;
      if (typeof p['total'] === 'number') t.subAgents.total = p['total'] as number;
      touch(t);
      return;
    }
    if (kind === 'SubAgentsBarrierReleased') {
      t.subAgents.waiting = false;
      const subs = Array.isArray(p['subs']) ? (p['subs'] as Array<Record<string, unknown>>) : [];
      for (const s of subs) {
        const subId = String(s['subId'] ?? s['callId'] ?? '');
        const ex = t.subAgents.items.find((i) => i.subId === subId);
        if (ex) ex.status = (s['success'] as boolean) ? 'succeeded' : 'failed';
      }
      t.subAgents.done = t.subAgents.total || t.subAgents.items.length;
      touch(t);
      return;
    }
  }

  // need04-03：分流刷新：tool-completed 只内存更新（调用方已标脏）；persisted/finish 才拉全量对账
  async function refreshAfterTool(targetSessionId?: string, reason?: string): Promise<void> {
    try {
      const sid = (targetSessionId ?? activeSessionId.value).trim();
      if (!sid) return;
      // 后台会话只落桶、不碰当前视图：避免把 B 的消息列表刷成 A 的（切卡时 selectSession 天然对账）
      if (sid !== activeSessionId.value) return;
      if (reason !== 'persisted' && reason !== 'finish') {
        await workspaceStore.refreshBudget();
        return;
      }
      await workspaceStore.loadSessionMessages(sid);
      await workspaceStore.refreshBudget();
    } catch (err) {
      console.error('[agentStore] refreshAfterTool failed:', err);
    }
  }

  async function finishTurn(targetSessionId?: string): Promise<void> {
    const sid = (targetSessionId ?? activeSessionId.value).trim();
    if (sid) {
      const t = ensureTurn(sid);
      // need04-04：轮末快照（03 管清，04 管快照）：供时间轴无缝占位，随后被持久 turnGroups 替换
      try {
        lastTurnSnapshot.value[sid] = { toolCards: [...t.toolCards], reasoning: t.streamingReasoning, text: t.streamingText, at: Date.now() };
        haloEventBus.emitLocal('agent:turn-archived', { sessionId: sid, snapshot: lastTurnSnapshot.value[sid] });
      } catch { /* 快照失败不阻断收尾 */ }
      if (dirtyPersisted.value[sid]) {
        await refreshAfterTool(sid, 'finish');
        dirtyPersisted.value[sid] = false;
      } else {
        await refreshAfterTool(sid, 'finish');
      }
      // need04-01：轮末轻量对账一次（每轮至多1次 getSessions，KB 级）
      try { await (workspaceStore as unknown as { reconcileSessionTimes?: () => Promise<void> }).reconcileSessionTimes?.(); } catch { /* 忽略 */ }
      // 清流式容器（工具卡移交快照后清空，消除轮末双影）
      t.toolCards = [];
      t.streamingText = '';
      t.streamingReasoning = '';
      t.streamingActive = false;
      touch(t);
    }
  }

  function isCompacting(sessionId: string): boolean {
    const t = getTurn(sessionId);
    return !!t && t.isGenerating && t.turnNotice.includes('压缩中');
  }

    /** need09-04：发送图片附件（可选第二参；老调用 sendPrompt(text) 仍为纯文本） */
    async function sendPrompt(text: string, attachments?: AgentSendAttachment[]): Promise<boolean> {
     const content = (text || '').trim();
     const sessionId = activeSessionId.value;
     const readyFiles = Array.isArray(attachments) ? attachments.filter((a) => a && a.base64) : [];
     if ((!content && readyFiles.length === 0) || !sessionId) return false;
     if (isCompacting(sessionId)) return false; // need05-03：压缩中拒绝发送
     if (ensureTurn(sessionId).isGenerating) return false;
 
     const workspaceId = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
     if (!workspaceId || !sessionId) return false;
 
     // need09-03/04：超限取最新 N 张（与后端网关一致，取新丢旧；0=默认4）
     let maxImages = Number(configStore.activeModel?.maxImagesPerTurn);
     if (!Number.isFinite(maxImages) || maxImages === 0) maxImages = 4;
     maxImages = Math.max(1, Math.min(16, Math.trunc(maxImages)));
     const kept = readyFiles.length > maxImages ? readyFiles.slice(readyFiles.length - maxImages) : readyFiles;
     const attachmentsJson = kept.length > 0 ? JSON.stringify(kept) : '';
 
     ensureListeners();
     resetTurnState(sessionId);
     touchSessionClock(sessionId);
     const t = ensureTurn(sessionId);
     t.isGenerating = true;
     t.streamingActive = true;
     touch(t);
 
     try {
       const bridge = getHostBridge();
       const providerId = configStore.activeProvider?.id || '';
       const modelId = configStore.activeModel?.id || '';
       const res = await bridge.startAgentTurn(workspaceId, sessionId, content, providerId, modelId, attachmentsJson);
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
    if (isCompacting(sessionId)) return; // need05-03：压缩中不可中断，中断按钮无效
    try {
      const bridge = getHostBridge();
      await bridge.cancelAgentTurn(sessionId);
    } catch (err) {
      console.error('[agentStore] stopGenerating failed:', err);
    } finally {
      // need09-06：问询等待期间停止 = 取消提问 + 取消整轮（后端 ct 联动转 Fail）
      try {
        const ask = useAskUserStore();
        if (ask.hasPending(sessionId)) ask.clearPending(sessionId);
      } catch { /* ignore */ }
      const t = ensureTurn(sessionId);
      t.isGenerating = false;
      t.streamingActive = false;
      t.turnNotice = '已停止生成。';
      touch(t);
      await refreshAfterTool(sessionId);
    }
  }

  function isBusyForHistory(sessionId: string): boolean {
    const t = getTurn(sessionId);
    if (!!t && (t.isGenerating || t.streamingActive || t.turnNotice.includes('压缩中'))) return true;
    // need09-06：问询 pending 视为忙（重放/分支/压缩/新发送一律拒绝，请先回答问题）
    try {
      return useAskUserStore().hasPending(sessionId);
    } catch {
      return false;
    }
  }

  // need07：重放 user（截断→重载→同文复发，避免双 user）
  async function replayUserMessage(msgId: string): Promise<boolean> {
    const ws = useWorkspaceStore();
    const sid = (activeSessionId.value || '').trim();
    if (!sid || isBusyForHistory(sid) || isCompacting(sid)) return false;
    const target = ws.sessionMessages.find((m) => String(m.id) === String(msgId));
    const text = String(target?.content || '').trim();
    if (!text) return false;
    try {
      const res = await getHostBridge().truncateSessionHistory(
        ws.activeWorkspaceId || ws.activeWorkspace?.id || '', sid, msgId, true);
      if (!res.ok || !res.data) return false;
      const replayText = String((res.data.replayText as unknown as string) || text);
      await ws.loadSessionMessages(sid);
      await ws.refreshBudget();
      return await sendPrompt(replayText);
    } catch (err) {
      console.error('[agentStore] replayUserMessage failed:', err);
      return false;
    }
  }

  // need07：重放 assistant（后端按前驱 user 切面，返回复发原文）
  async function replayAssistantTurn(assistantId: string): Promise<boolean> {
    const ws = useWorkspaceStore();
    const sid = (activeSessionId.value || '').trim();
    if (!sid || isBusyForHistory(sid) || isCompacting(sid)) return false;
    try {
      const res = await getHostBridge().truncateSessionHistory(
        ws.activeWorkspaceId || ws.activeWorkspace?.id || '', sid, assistantId, true);
      if (!res.ok || !res.data) return false;
      const replayText = String((res.data.replayText as unknown as string) || '').trim();
      if (!replayText) return false;
      await ws.loadSessionMessages(sid);
      await ws.refreshBudget();
      return await sendPrompt(replayText);
    } catch (err) {
      console.error('[agentStore] replayAssistantTurn failed:', err);
      return false;
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
    hasActiveTurn,
    hiddenToolCallIds,
    subAgents,
    dirtyPersisted,
    lastTurnSnapshot,
    handleTurnEvent,
    finishTurn,
    refreshAfterTool,
    bind,
    resetTurnState,
    isCompacting,
    sendPrompt,
    replayUserMessage,
    replayAssistantTurn,
    stopGenerating,
  };
});
