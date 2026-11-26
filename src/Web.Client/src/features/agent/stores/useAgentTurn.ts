/** need21-F03：轮次 store（发送/取消/requestId/轮生命周期编排；依赖最多最后拆）。
 * 发送经 @shared/bridge/api/agent 的 startAgentTurn 对象参；跨 store 读经 runtime 上下文 + haloEventBus 事件，禁止直 import 其他 store。
 */
import { defineStore } from 'pinia';
import { computed } from 'vue';
import { getBridgeTransport, getHistoryCapabilitiesState, haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import { historyErrorMessage } from '@shared/bridge/protocol/errors';
import type { HistoryReplayResult, HistoryReplayImageDto } from '@shared/bridge/protocol/domain';
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
  type AgentSendMedia,
  type PendingReplay,
  type AgentTurnState,
  type TurnEventPayload,
} from './model';
import { agentRuntime } from './runtime';
import { dropSegBuf } from './useAgentStream';
import {
  applyStreamError,
  cancelPendingReconcile,
  finishTurn,
  handleTurnLifecycle,
  isBusyForHistory,
  isCompacting,
  refreshAfterTool,
  stopGenerating,
} from './turnEvents';
import { readNeed02Flags } from '@features/need02/utils/need02Flags';
import { debug as need02Debug } from '@features/need02/utils/need02Debug';
import type { PendingUser } from './model';
import type { ChatImageRef } from '@shared/bridge/hostBridge';

function makePendingKey(requestId: string): string {
  return `pending-${String(requestId || '').trim()}`;
}

function makePendingUser(args: {
  requestId: string;
  sessionId: string;
  content: string;
  images?: ChatImageRef[];
  previews?: Record<string, string>;
  createdAtUtc?: string;
}): PendingUser {
  const requestId = String(args.requestId || '').trim();
  const sessionId = String(args.sessionId || '').trim();
  return {
    key: makePendingKey(requestId),
    requestId,
    sessionId,
    content: String(args.content ?? ''),
    images: Array.isArray(args.images) ? args.images.slice() : [],
    previews: args.previews ? { ...args.previews } : {},
    createdAtUtc: args.createdAtUtc || new Date().toISOString(),
    status: 'sending',
    errorCode: null,
    confirmedId: null,
  };
}

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
  async function sendPromptForSession(
    workspaceIdParam: string,
    sessionIdParam: string,
    text: string,
    media?: AgentSendMedia[]
  ): Promise<boolean> {
    const content = (text || '').trim();
    const sessionId = (sessionIdParam || '').trim();
    const workspaceId = (workspaceIdParam || '').trim();
    const allMedia = Array.isArray(media) ? media.slice() : [];
    const readyFiles = allMedia.filter((m) => m.kind !== 'stored') as AgentSendAttachment[];
    if ((!content && allMedia.length === 0) || !workspaceId || !sessionId) return false;
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
    // 超限取最新 N 张（与后端网关一致，取新丢旧；上限由 runtime 侧配置收敛）
    const maxImages = rt.maxImages();
    const kept = readyFiles.length > maxImages ? readyFiles.slice(readyFiles.length - maxImages) : readyFiles;
    const serialized = allMedia.map((item) =>
      item.kind === 'stored'
        ? { kind: 'stored', imageId: item.imageId, fileName: item.fileName, mime: item.mime }
        : {
            kind: 'base64',
            attId: (item as AgentSendAttachment).attId,
            fileName: item.fileName,
            mime: item.mime,
            base64: (item as AgentSendAttachment).base64,
          }
    );
    const keptMedia = serialized.length > maxImages ? serialized.slice(serialized.length - maxImages) : serialized;
    const attachmentsJson = keptMedia.length > 0 ? JSON.stringify(keptMedia) : '';
    const requestId = newRequestId();
    // resetTurnState 内联（旧语义；分片缓冲归 stream 侧）
    // 新发送 supersede 旧 cancelled/sending pending：旧对账重试不得再触新轮。
    cancelPendingReconcile(sessionId);
    dropSegBuf(sessionId);
    // 重建轮状态时保留截断恢复项：发送失败后仍需可从原会话继续重放。
    const preservedPendingReplay = getTurn(sessionId)?.pendingReplay ?? null;
    turns.value[sessionId] = emptyTurnState(sessionId, fallbackProtocol.value);
    turns.value[sessionId].pendingReplay = preservedPendingReplay;
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
    t.lastMedia = allMedia.slice();
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
        if (res.code === 'HISTORY_READONLY') {
          if (p1On) markPendingFailed(t, 'HISTORY_READONLY');
          t.lastError = res.message || '历史版本只读，不可继续对话，可分支';
          t.lastErrorInfo = { code: 'HISTORY_READONLY', message: t.lastError, retry: false };
          t.isGenerating = false;
          t.streamingActive = false;
          t.requestId = '';
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
      // 发送链保留旧预算，切会话仍清空
      await rt.loadSessionMessages(sessionId, { preserveBudget: true });
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

  /** 当前会话发送入口：重放等跨异步操作必须调用带工作区/会话的版本，避免串线。 */
  async function sendPrompt(text: string, attachments?: AgentSendAttachment[]): Promise<boolean> {
    return sendPromptForSession(activeWorkspaceId.value, activeSessionId.value, text, attachments);
  }

  /** 发送失败重试：保留本轮完整媒体，并使用新的 requestId。 */
  async function retryLastTurn(targetSessionId?: string): Promise<boolean> {
    void targetSessionId;
    const sid = activeSessionId.value.trim();
    if (!sid) return false;
    const t = ensureTurn(sid);
    if (t.isGenerating) return false;
    const text = (t.lastPrompt || '').trim();
    const media = Array.isArray(t.lastMedia) ? t.lastMedia.slice() : [];
    if (!text && media.length === 0) return false;
    t.lastError = '';
    t.lastErrorInfo = null;
    touch(t);
    return sendPromptForSession(activeWorkspaceId.value, sid, text, media);
  }

  /** need02-P1：replay/branch/delete 先清本 session pending，避免旧乐观串入新历史。 */
  function clearPending(sessionId?: string): void {
    const sid = (sessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    cancelPendingReconcile(sid);
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

  function replayFailure(code: string, message: string): HistoryReplayResult {
    return {
      ok: false,
      code,
      message,
      replayText: '',
      replayImages: [],
      destructiveCommitCompleted: false,
      warnings: [],
    };
  }

  function isTargetCurrent(workspaceId: string, sessionId: string): boolean {
    return activeWorkspaceId.value === workspaceId && activeSessionId.value === sessionId;
  }

  function toStoredMedia(images: HistoryReplayImageDto[]): AgentSendMedia[] {
    return images
      .filter((image) => image && !image.lost && !!image.imageId)
      .map((image) => ({
        kind: 'stored' as const,
        imageId: image.imageId,
        fileName: image.fileName,
        mime: image.mime,
      }));
  }

  /** 截断是破坏性提交，发送是独立后续步骤；任何一步失败都保留恢复项并准确说明。 */
  async function runReplay(anchorMessageId: string): Promise<HistoryReplayResult> {
    const rt = agentRuntime();
    const workspaceId = (rt.workspaceId() || '').trim();
    const sessionId = (activeSessionId.value || '').trim();
    const mid = (anchorMessageId || '').trim();
    if (!workspaceId || !sessionId || !mid) {
      return replayFailure('ANCHOR_NOT_FOUND', historyErrorMessage('ANCHOR_NOT_FOUND'));
    }
    if (isBusyForHistory(sessionId) || isCompacting(sessionId)) {
      return replayFailure('SESSION_BUSY', historyErrorMessage('SESSION_BUSY'));
    }
    const capabilities = getHistoryCapabilitiesState();
    if (!capabilities.ready || !capabilities.capabilities.historyAnchors) {
      return replayFailure('UNSUPPORTED_HISTORY', '当前后端版本不支持历史重放，请升级');
    }
    if (!capabilities.capabilities.replayImages) {
      return replayFailure('UNSUPPORTED_HISTORY', '当前后端不支持安全重放，请升级');
    }
    clearPending(sessionId);
    try {
      const result = await rt.truncateHistory(workspaceId, sessionId, mid, true);
      if (!result.ok) {
        const stable = historyErrorMessage(result.code, '');
        return { ...result, message: stable || result.message || '历史操作失败' };
      }
      if (!result.destructiveCommitCompleted) {
        return replayFailure('TRUNCATE_COMMIT_ERROR', historyErrorMessage('TRUNCATE_COMMIT_ERROR'));
      }
      const t = ensureTurn(sessionId);
      const media = toStoredMedia(result.replayImages || []);
      const warnings = [
        ...(result.warnings || []),
        ...(result.replayImages || [])
          .filter((image) => image && image.lost)
          .map((image) => ({
            code: 'IMAGE_MISSING' as const,
            message: `图片 ${image.fileName || image.imageId} 已缺失，未参与重放`,
          })),
      ];
      const pending: PendingReplay = {
        workspaceId,
        sessionId,
        anchorMessageId: mid,
        text: result.replayText,
        media,
        recoveryToken: result.recoveryToken,
        createdAt: Date.now(),
      };
      t.pendingReplay = pending;
      touch(t);

      if (!isTargetCurrent(workspaceId, sessionId)) {
        return {
          ...result,
          ok: false,
          code: 'REPLAY_TARGET_CHANGED',
          message: historyErrorMessage('REPLAY_TARGET_CHANGED'),
          warnings,
        };
      }
      try {
        await rt.loadSessionMessages(sessionId);
        await rt.refreshBudget();
      } catch {
        return {
          ...result,
          ok: false,
          code: 'REPLAY_SEND_FAILED',
          message: '历史已截断，刷新失败，可稍后继续重放',
          warnings,
        };
      }
      if (!isTargetCurrent(workspaceId, sessionId)) {
        return {
          ...result,
          ok: false,
          code: 'REPLAY_TARGET_CHANGED',
          message: historyErrorMessage('REPLAY_TARGET_CHANGED'),
          warnings,
        };
      }
      const sent = await sendPromptForSession(workspaceId, sessionId, result.replayText, media);
      if (!sent) {
        return {
          ...result,
          ok: false,
          code: 'REPLAY_SEND_FAILED',
          message: historyErrorMessage('REPLAY_SEND_FAILED'),
          warnings,
        };
      }
      const current = getTurn(sessionId);
      if (current) {
        current.pendingReplay = null;
        touch(current);
      }
      return { ...result, warnings };
    } catch (err) {
      return replayFailure('REPLAY_SEND_FAILED', err instanceof Error ? err.message : String(err));
    }
  }

  async function replayUserMessage(msgId: string): Promise<HistoryReplayResult> {
    return runReplay(msgId);
  }

  async function replayAssistantTurn(assistantId: string): Promise<HistoryReplayResult> {
    return runReplay(assistantId);
  }

  /** 从截断后保留的恢复项继续发送：再次确认目标会话与能力，使用新 requestId。 */
  async function resumePendingReplay(): Promise<HistoryReplayResult> {
    const sessionId = activeSessionId.value.trim();
    const workspaceId = activeWorkspaceId.value.trim();
    const t = getTurn(sessionId);
    const pending: PendingReplay | null = t?.pendingReplay ?? null;
    if (!pending || pending.sessionId !== sessionId || pending.workspaceId !== workspaceId) {
      return replayFailure('REPLAY_TARGET_CHANGED', '当前会话没有待继续的重放');
    }
    const capabilities = getHistoryCapabilitiesState();
    if (!capabilities.ready || !capabilities.capabilities.historyAnchors) {
      return replayFailure('UNSUPPORTED_HISTORY', '当前后端版本不支持历史重放，请升级');
    }
    clearPending(sessionId);
    const sent = await sendPromptForSession(workspaceId, sessionId, pending.text, pending.media);
    if (!sent) return replayFailure('REPLAY_SEND_FAILED', historyErrorMessage('REPLAY_SEND_FAILED'));
    const current = getTurn(sessionId);
    if (current) {
      current.pendingReplay = null;
      touch(current);
    }
    return {
      ok: true,
      replayText: pending.text,
      replayImages: [],
      destructiveCommitCompleted: true,
      warnings: [],
    };
  }

  function discardPendingReplay(): void {
    const t = getTurn(activeSessionId.value);
    if (!t?.pendingReplay) return;
    t.pendingReplay = null;
    touch(t);
  }

  const currentPendingReplay = computed<PendingReplay | null>(() => getTurn(activeSessionId.value)?.pendingReplay ?? null);

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
    sendPromptForSession,
    replayUserMessage,
    replayAssistantTurn,
    resumePendingReplay,
    discardPendingReplay,
    currentPendingReplay,
    stopGenerating,
    bindTurn,
  };
});
