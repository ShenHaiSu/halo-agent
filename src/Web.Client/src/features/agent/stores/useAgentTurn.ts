/** need21-F03：轮次 store（发送/取消/requestId/轮生命周期编排；依赖最多最后拆）。
 * 发送经 @shared/bridge/api/agent 的 startAgentTurn 对象参；跨 store 读经 runtime 上下文 + haloEventBus 事件，禁止直 import 其他 store。
 */
import { defineStore } from 'pinia';
import { computed } from 'vue';
import { getBridgeTransport, haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import { startAgentTurn } from '@shared/bridge/api/agent';
import {
  activeSessionId,
  activeWorkspaceId,
  dirtyPersisted,
  emptyTurnState,
  ensureSessionSync,
  ensureTurn,
  fallbackProtocol,
  getTurn,
  lastTurnSnapshot,
  markSeenRequest,
  newRequestId,
  resolveTargetSession,
  seenRequestIds,
  toast,
  touch,
  turnStartMarks,
  turns,
  type AgentSendAttachment,
  type AgentTurnState,
  type TurnEventPayload,
} from './model';
import { agentRuntime } from './runtime';
import { dropSegBuf } from './useAgentStream';
import {
  applyStreamError,
  finishTurn,
  handleTurnLifecycle,
  isBusyForHistory,
  isCompacting,
  refreshAfterTool,
  stopGenerating,
} from './turnEvents';
import { readNeed02Flags } from '@features/need02/utils/need02Flags';
import { debug as need02Debug } from '@features/need02/utils/need02Debug';
import { makePendingUser, type PendingUser } from '@features/session/utils/pendingUser';
import type { ChatImageRef } from '@shared/bridge/hostBridge';

let listenersBound = false;

function bindTurnListeners(): void {
  if (listenersBound) return;
  listenersBound = true;
  ensureSessionSync();
  haloEventBus.on<SessionStreamEventDto>('llm:stream-event', (evt) => {
    if (!evt || typeof evt.kind !== 'string') return;
    if (evt.kind === 'StreamError') applyStreamError(evt);
  });
  haloEventBus.on<TurnEventPayload>('agent:turn-event', (evt) => {
    if (!evt || typeof evt.kind !== 'string') return;
    const sid = resolveTargetSession(evt.sessionId);
    if (!sid) return;
    handleTurnLifecycle(evt.kind, evt.payload || {}, sid);
  });
}

export const useAgentTurn = defineStore('agent-turn', () => {
  bindTurnListeners();

  const activeTurn = computed<AgentTurnState>(() => ensureTurn(activeSessionId.value));

  const activeKey = computed(() => `${activeWorkspaceId.value}::${activeSessionId.value}`);

  // ---- 兼容别名 ----
  const turnNotice = computed<string>({
    get: () => activeTurn.value.turnNotice,
    set: (v) => {
      activeTurn.value.turnNotice = v;
    },
  });
  const lastError = computed<string>({
    get: () => activeTurn.value.lastError,
    set: (v) => {
      activeTurn.value.lastError = v;
    },
  });

  const runningSessionIds = computed<string[]>(() =>
    Object.values(turns.value)
      .filter((t) => t.isGenerating || t.streamingActive)
      .map((t) => t.sessionId)
  );
  const isAnyGenerating = computed(() => runningSessionIds.value.length > 0);

  /** need22-P2 F2：幂等发送。生成 requestId=uuid 透传后端；连点第二发 toast + 忽略，不起新轮。 */
  /** need02-P1：p1 on 时发送瞬间插入乐观 pending（≤100ms 首现），后端回包后由 workspace 对账转正。 */
  function buildPendingImages(kept: AgentSendAttachment[]): { images: ChatImageRef[]; previews: Record<string, string> } {
    const images: ChatImageRef[] = [];
    const previews: Record<string, string> = {};
    for (const a of kept) {
      const attId = String((a as AgentSendAttachment).attId || '').trim();
      if (!attId) continue;
      const mime = String((a as AgentSendAttachment).mime || 'image/png');
      const b64 = String((a as AgentSendAttachment).base64 || '');
      images.push({
        imageId: attId,
        fileName: String((a as AgentSendAttachment).fileName || attId),
        mime,
        relPath: '',
        width: 0,
        height: 0,
        sizeBytes: b64.length,
        sha16: attId,
      });
      if (b64) previews[attId] = `data:${mime};base64,${b64}`;
    }
    return { images, previews };
  }
  function isP1On(): boolean {
    try {
      return readNeed02Flags().p1;
    } catch {
      return true;
    }
  }
  /** need02-P1：重发必须新 requestId（R4：后端去重字典会吞复用 requestId）。旧 failed 标记 superseded，不复用。 */
  function markPendingFailed(t: AgentTurnState, code: string | null): void {
    if (t.pendingUser) {
      t.pendingUser.status = 'failed';
      t.pendingUser.errorCode = code;
      touch(t);
    }
  }
  async function sendPrompt(text: string, attachments?: AgentSendAttachment[]): Promise<boolean> {
    const content = (text || '').trim();
    const sessionId = activeSessionId.value;
    const readyFiles = Array.isArray(attachments) ? attachments.filter((a) => a && (a as AgentSendAttachment).base64) : [];
    if ((!content && readyFiles.length === 0) || !sessionId) return false;
    if (isCompacting(sessionId)) {
      toast('warning', '压缩中，新一轮请稍候');
      return false;
    } // need05-03：压缩中拒绝发送
    const cur = ensureTurn(sessionId);
    if (cur.isGenerating) {
      // 连点/重入：toast 既有 requestId，不起新轮（后端同时回 TURN_IN_PROGRESS 双保险）
      toast('warning', `上一轮仍在进行中（requestId=${cur.requestId || 'unknown'}），本发已忽略`);
      return false;
    }
    const p1On = isP1On();
    const prevRequestId = cur.requestId || '';
    const prevPrompt = cur.lastPrompt || '';
    const rt = agentRuntime();
    const workspaceId = rt.workspaceId();
    if (!workspaceId || !sessionId) return false;
    // need09-03/04：超限取最新 N 张（与后端网关一致，取新丢旧；上限由 runtime 侧配置收敛）
    const maxImages = rt.maxImages();
    const kept = readyFiles.length > maxImages ? readyFiles.slice(readyFiles.length - maxImages) : readyFiles;
    const attachmentsJson = kept.length > 0 ? JSON.stringify(kept) : '';
    const requestId = newRequestId();
    // resetTurnState 内联（旧语义；分片缓冲归 stream 侧）
    dropSegBuf(sessionId);
    turns.value[sessionId] = emptyTurnState(sessionId, fallbackProtocol.value);
    turnStartMarks[sessionId] = Date.now();
    try {
      rt.touchSessionTime(sessionId);
    } catch {
      /* 时间刷新永不阻断主链路 */
    }
    const t = ensureTurn(sessionId);
    // need02-P1：先插 pending 再调桥（p1 off 时回旧链：不插 pending）。
    let pending: PendingUser | null = null;
    if (p1On) {
      const { images, previews } = buildPendingImages(kept);
      pending = makePendingUser({ requestId, sessionId, content, images, previews });
      t.pendingUser = pending;
      try {
        need02Debug('pending-insert', { requestId, at: Date.now(), contentLen: content.length, images: images.length });
        haloEventBus.emitLocal('need02:pending', { sessionId, requestId, key: pending.key });
      } catch {
        /* 埋点/事件永不阻断主链路 */
      }
    }
    t.requestId = requestId;
    markSeenRequest(requestId);
    t.isGenerating = true;
    t.streamingActive = true;
    t.lastErrorInfo = null;
    t.lastPrompt = content;
    t.lastAttachments = kept.slice();
    touch(t);
    try {
      const res = await startAgentTurn(getBridgeTransport(), {
        workspaceId,
        sessionId,
        userText: content,
        providerId: rt.providerId(),
        modelId: rt.modelId(),
        attachmentsJson,
        requestId,
      });
      if (!res.ok) {
        if (res.code === 'TURN_IN_PROGRESS') {
          // 后端重入拒绝：旧轮不受影响，恢复旧 requestId/原文继续跟旧轮
          t.requestId = prevRequestId;
          t.lastPrompt = prevPrompt || t.lastPrompt;
          if (p1On) markPendingFailed(t, 'TURN_IN_PROGRESS');
          t.lastError = res.message || '上一轮仍在进行中';
          t.lastErrorInfo = { code: 'TURN_IN_PROGRESS', message: t.lastError, retry: false };
          toast('warning', t.lastError);
          touch(t);
          return false;
        }
        if (res.code === 'COMPACTING') {
          // need02-P1：COMPACTING 发送前即拒，pending 不留（回滚即无痕）。
          if (p1On) t.pendingUser = null;
          t.lastError = res.message || '该会话正在压缩上下文';
          t.lastErrorInfo = { code: 'COMPACTING', message: t.lastError, retry: false };
          t.isGenerating = false;
          t.streamingActive = false;
          t.requestId = '';
          toast('warning', t.lastError);
          touch(t);
          return false;
        }
        if (p1On) markPendingFailed(t, res.code || 'AGENT_START_ERROR');
        t.lastError = res.message || '启动 Agent 推理失败';
        t.lastErrorInfo = { code: res.code || 'AGENT_START_ERROR', message: t.lastError, retry: true };
        t.isGenerating = false;
        t.streamingActive = false;
        t.requestId = '';
        toast('error', t.lastError);
        touch(t);
        return false;
      }
      // 乐观回显用户消息，等待 TurnCompleted 后与 jsonl 对账
      await rt.loadSessionMessages(sessionId);
      return true;
    } catch (err) {
      console.error('[agent-turn] sendPrompt failed:', err);
      if (p1On) markPendingFailed(t, 'SEND_FAILED');
      t.lastError = err instanceof Error ? err.message : String(err);
      t.lastErrorInfo = { code: 'SEND_FAILED', message: t.lastError, retry: true };
      t.isGenerating = false;
      t.streamingActive = false;
      t.requestId = '';
      toast('error', t.lastError);
      touch(t);
      return false;
    }
  }

  /** need22-P2 F5：失败重试（用 lastPrompt 原文重发；成功清错误态）。 */
  /** need02-P1：重试带图（lastAttachments），新 requestId，旧 failed 不复用。p1 off 时回旧链（仅文本）。 */
  async function retryLastTurn(targetSessionId?: string): Promise<boolean> {
    void targetSessionId;
    const sid = activeSessionId.value.trim();
    if (!sid) return false;
    const t = ensureTurn(sid);
    if (t.isGenerating) return false;
    const text = (t.lastPrompt || '').trim();
    const atts = Array.isArray(t.lastAttachments) ? t.lastAttachments.slice() : [];
    if (!text && atts.length === 0) return false;
    t.lastError = '';
    t.lastErrorInfo = null;
    touch(t);
    if (isP1On() && atts.length > 0) return sendPrompt(text, atts);
    return sendPrompt(text);
  }

  /** need02-P1：replay/branch/delete 先清本 session pending，避免旧乐观串入新历史。 */
  function clearPending(sessionId?: string): void {
    const sid = (sessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    const t = getTurn(sid);
    if (t && t.pendingUser) {
      t.pendingUser = null;
      touch(t);
    }
  }

  function clearLastError(targetSessionId?: string): void {
    const sid = (targetSessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    const t = ensureTurn(sid);
    t.lastError = '';
    t.lastErrorInfo = null;
    touch(t);
  }

  // need07：重放 user（截断→重载→同文复发，避免双 user）
  async function replayUserMessage(msgId: string): Promise<boolean> {
    const rt = agentRuntime();
    const sid = (activeSessionId.value || '').trim();
    if (!sid || isBusyForHistory(sid) || isCompacting(sid)) return false;
    clearPending(sid);
    const text = rt.findMessageContent(sid, msgId).trim();
    if (!text) return false;
    try {
      const res = await rt.truncateHistory(rt.workspaceId(), sid, msgId, true);
      if (!res.ok) return false;
      const replayText = String(res.replayText || text);
      await rt.loadSessionMessages(sid);
      await rt.refreshBudget();
      return await sendPrompt(replayText);
    } catch (err) {
      console.error('[agent-turn] replayUserMessage failed:', err);
      return false;
    }
  }

  // need07：重放 assistant（后端按前驱 user 切面，返回复发原文）
  async function replayAssistantTurn(assistantId: string): Promise<boolean> {
    const rt = agentRuntime();
    const sid = (activeSessionId.value || '').trim();
    if (!sid || isBusyForHistory(sid) || isCompacting(sid)) return false;
    clearPending(sid);
    try {
      const res = await rt.truncateHistory(rt.workspaceId(), sid, assistantId, true);
      if (!res.ok) return false;
      const replayText = String(res.replayText || '').trim();
      if (!replayText) return false;
      await rt.loadSessionMessages(sid);
      await rt.refreshBudget();
      return await sendPrompt(replayText);
    } catch (err) {
      console.error('[agent-turn] replayAssistantTurn failed:', err);
      return false;
    }
  }

  // ---- 兼容：旧 handleTurnEvent 公开签名（生命周期分支；工具/子代理/Iteration 分支由各 store 事件订阅处理） ----
  function handleTurnEvent(kind: string, payload: Record<string, unknown>, sessionId?: string): void {
    handleTurnLifecycle(kind, payload, sessionId);
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

  function bindTurn(): void {
    bindTurnListeners();
  }

  return {
    turns,
    activeTurn,
    activeSessionId,
    activeKey,
    runningSessionIds,
    isAnyGenerating,
    ensureTurn,
    getTurn,
    seenRequestIds,
    turnNotice,
    lastError,
    dirtyPersisted,
    lastTurnSnapshot,
    handleTurnEvent,
    finishTurn,
    refreshAfterTool,
    queryActiveTurns,
    subscribe,
    pauseTurn,
    resumeTurn,
    retryLastTurn,
    clearPending,
    clearLastError,
    isCompacting,
    sendPrompt,
    replayUserMessage,
    replayAssistantTurn,
    stopGenerating,
    bindTurn,
  };
});
