/** need21-F03：agent 域共享瞬态模型（非 store：类型 + 单例 state + 纯函数 + 运行时上下文 DI）。
 * 4 小 store 与旧转发共用同一份 state，响应式语义与旧 agentStore.ts 一致；跨 store 读经此处参数/事件，不直 import 其他 store。
 */
import { ref } from 'vue';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { haloEventBus } from '@shared/bridge/hostBridge';

export type StreamSegKind = 'text' | 'reasoning' | 'tool';
export interface StreamSeg {
  key: string;
  kind: StreamSegKind;
  iteration: number;
  text?: string;
  callId?: string;
  persisted?: boolean;
}
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
  images?: ChatImageRef[];
  /** need23-R1：该工具消息在 messages.jsonl 中的 id（空=尚未落盘）。 */
  messageId?: string;
  /** need23-R1：结果是否已落盘（ToolMessagePersisted 到达后为 true）。 */
  persisted?: boolean;
}

/** need09-04：sendPrompt 可选第二参附件项 */
export interface AgentSendAttachment {
  attId: string;
  fileName: string;
  mime: string;
  base64: string;
}

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
  /** need22-P2 F5：结构化错误（code/message/retry），lastError 字符串保留兼容模板 */
  lastErrorInfo: { code: string; message: string; retry: boolean } | null;
  /** need22-P2 F2：本轮 requestId（uuid，去重键；空=无进行中轮） */
  requestId: string;
  /** need22-P2 F5：失败重试用原文（成功/新发后清空） */
  lastPrompt: string;
  updatedAt: number;
  /** need13：主会话子进度（瞬态，不持久化） */
  subAgents: { total: number; done: number; waiting: boolean; items: Array<{ subId: string; label: string; status: string }> };
  curIteration: number;
  segs: StreamSeg[];
  toolCardIndex: Record<string, number>;
}

export interface TurnEventPayload {
  sessionId: string;
  kind: string;
  payload?: Record<string, unknown> | null;
  timestamp?: number;
}

// ---- 单例 state（旧 store 内 ref 上移，4 store 共享同一份） ----
/** 按 sessionId 分桶的瞬态（唯一真相；后台会话事件按 evt.sessionId 落桶）。 */
export const turns = ref<Record<string, AgentTurnState>>({});
/** need04-03/04：本轮去重与归并快照 */
export const dirtyPersisted = ref<Record<string, boolean>>({});
export const lastTurnSnapshot = ref<Record<string, { toolCards: AgentToolCard[]; reasoning: string; text: string; at: number }>>({});
/** need22-P2 F2：已见 requestId（后端回显去重；有界防涨） */
export const seenRequestIds = ref<Record<string, number>>({});
/** 当前会话（旧转发自 sessionStore 同步 + session:switched 事件；store 内只读此 ref）。 */
export const activeSessionId = ref<string>('');
/** 当前工作区（旧转发自 workspaceStore 同步；仅 activeKey 用）。 */
export const activeWorkspaceId = ref<string>('');
/** 回退协议（旧转发自 configStore 同步；resetTurnState 用）。 */
export const fallbackProtocol = ref<string>('chat');
/** 持久消息投影（旧转发自 workspaceStore.sessionMessages 同步；归并 getter 用，F04 改事件全量）。 */
export const persistedMessages = ref<UnifiedChatMessage[]>([]);

export const turnStartMarks: Record<string, number> = {};

export function emptyTelemetry(protocol = 'chat'): AgentTelemetry {
  return {
    protocol,
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

export function emptyTurnState(sessionId: string, protocol = 'chat'): AgentTurnState {
  return {
    sessionId,
    streamingText: '',
    streamingReasoning: '',
    streamingActive: false,
    isGenerating: false,
    toolCards: [],
    telemetry: emptyTelemetry(protocol),
    turnNotice: '',
    lastError: '',
    lastErrorInfo: null,
    requestId: '',
    lastPrompt: '',
    updatedAt: Date.now(),
    subAgents: { total: 0, done: 0, waiting: false, items: [] },
    curIteration: 0,
    segs: [],
    toolCardIndex: {},
  };
}

export function ensureTurn(sessionId: string): AgentTurnState {
  const sid = (sessionId || '').trim();
  if (!sid) return emptyTurnState('');
  let t = turns.value[sid];
  if (!t) {
    t = emptyTurnState(sid, fallbackProtocol.value);
    turns.value[sid] = t;
  }
  return t;
}

export function getTurn(sessionId: string): AgentTurnState | null {
  const sid = (sessionId || '').trim();
  if (!sid) return null;
  return turns.value[sid] || null;
}

export function touch(t: AgentTurnState): void {
  t.updatedAt = Date.now();
}

export function rebuildIndex(t: AgentTurnState): void {
  const m: Record<string, number> = {};
  for (let i = 0; i < t.toolCards.length; i++) {
    const c = t.toolCards[i];
    if (c && c.callId) m[c.callId] = i;
  }
  t.toolCardIndex = m;
}

export function cardById(t: AgentTurnState, callId: string): AgentToolCard | null {
  const i = t.toolCardIndex[callId];
  if (i === undefined) return t.toolCards.find((c) => c.callId === callId) || null;
  return t.toolCards[i] || null;
}

export function truncateText(s: string, max = 6000): string {
  if (!s) return '';
  return s.length > max ? s.slice(0, max) + '\n...（输出过长已截断）' : s;
}

export function toast(kind: 'info' | 'success' | 'warning' | 'error', text: string): void {
  try {
    (window as unknown as { $message?: Record<string, (s: string) => void> }).$message?.[kind]?.(text);
  } catch {
    /* 无消息通道时静默 */
  }
}

/** need22-P2 F2：uuid（crypto 优先，降级 Math.random）。 */
export function newRequestId(): string {
  try {
    const c = (globalThis as unknown as { crypto?: { randomUUID?: () => string } }).crypto;
    if (c && typeof c.randomUUID === 'function') return c.randomUUID().replace(/-/g, '');
  } catch {
    /* fall through */
  }
  return `req_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 12)}`;
}

export function markSeenRequest(reqId: string): void {
  const id = (reqId || '').trim();
  if (!id) return;
  seenRequestIds.value[id] = Date.now();
  const keys = Object.keys(seenRequestIds.value);
  if (keys.length > 500) {
    for (const k of keys.slice(0, keys.length - 500)) delete seenRequestIds.value[k];
  }
}

function segKeyOf(sid: string, kind: string, iteration: number, n: number): string {
  return sid.slice(-6) + '-' + String(iteration) + '-' + kind + '-' + String(n);
}
export function tailSeg(t: AgentTurnState): StreamSeg | null {
  return t.segs.length ? t.segs[t.segs.length - 1] : null;
}
export function pushTextSeg(t: AgentTurnState, kind: 'text' | 'reasoning', chunk: string): void {
  const tail = tailSeg(t);
  if (tail && tail.kind === kind && tail.iteration === t.curIteration && !tail.persisted) {
    tail.text = (tail.text || '') + chunk;
    return;
  }
  const n = t.segs.length;
  t.segs.push({ key: segKeyOf(t.sessionId, kind, t.curIteration, n), kind, iteration: t.curIteration, text: chunk });
}
export function pushToolSeg(t: AgentTurnState, callId: string): void {
  if (!callId) return;
  const tail = tailSeg(t);
  if (tail && tail.kind === 'tool' && tail.callId === callId && tail.iteration === t.curIteration) return;
  t.segs.push({ key: segKeyOf(t.sessionId, 'tool', t.curIteration, t.segs.length), kind: 'tool', iteration: t.curIteration, callId });
}
export function markIterationPersisted(t: AgentTurnState, iteration: number): void {
  if (!iteration) return;
  for (const s of t.segs) {
    if (s.iteration === iteration) s.persisted = true;
  }
}
/** 事件目标会话：evt.sessionId 为空回退当前会话（旧 resolveTargetSession 语义；三 store 事件路由共用）。 */
export function resolveTargetSession(evtSessionId: string | undefined | null): string {
  const s = (evtSessionId || '').trim();
  if (s) return s;
  return activeSessionId.value;
}

// ---- 旧转发同步入口（供 stores/agentStore.ts；新 store 不直读其他 store） ----
export function setActiveSessionId(sid: string): void {
  activeSessionId.value = (sid || '').trim();
}
export function setActiveWorkspaceId(wid: string): void {
  activeWorkspaceId.value = (wid || '').trim();
}
export function setFallbackProtocol(p: string): void {
  if (p) fallbackProtocol.value = p;
}
export function setPersistedMessages(msgs: UnifiedChatMessage[]): void {
  persistedMessages.value = Array.isArray(msgs) ? msgs : [];
}
let sessionSyncBound = false;
/** 各小 store bind 时调用：订阅 session:switched（F04 起由 session 域 emit；当前由旧转发桥接 emit）。 */
export function ensureSessionSync(): void {
  if (sessionSyncBound) return;
  sessionSyncBound = true;
  haloEventBus.on<{ sessionId?: string; workspaceId?: string }>('session:switched', (p) => {
    try {
      if (p && typeof p.sessionId === 'string') setActiveSessionId(p.sessionId);
      if (p && typeof p.workspaceId === 'string') setActiveWorkspaceId(p.workspaceId);
    } catch {
      /* 同步永不阻断 */
    }
  });
}
