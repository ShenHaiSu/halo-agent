/** need21-F03：turn 生命周期事件归约（自旧 agentStore handleTurnEvent/finishTurn 原样搬入）。
 * 后端回执分支（Persisted/Completed/Cancelled/Error/Truncated/Compact/StreamError）唯一归约点；
 * workspace/session 侧效经 runtime 上下文（旧转发注入实现），本模块不直 import 其他 store。
 */
import { getHostBridge, haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import {
  activeSessionId,
  dirtyPersisted,
  ensureTurn,
  finalizePending,
  getTurn,
  lastTurnSnapshot,
  markIterationTextPersisted,
  markSoleFinal,
  resolveTargetSession,
  toast,
  touch,
  turnStartMarks,
} from './model';
import { agentRuntime } from './runtime';
import { finalizeSubAgents } from './subAgentStatus';
import { dropSegBuf, flushSegBufSync } from './useAgentStream';
import { debug as need02Debug } from '@features/need02/utils/need02Debug';

/** need04-01：任何写消息路径结束时内存态秒级刷新会话卡时间（乐观+事件双通道）。 */
function touchSessionClock(sessionId: string, iso?: string): void {
  try {
    agentRuntime().touchSessionTime(sessionId, iso);
  } catch {
    /* 时间刷新永不阻断主链路 */
  }
}

// 轮末交接：终态已就位后流容器延迟 300ms 再清，供交接编排做交叉淡入淡出。

/** need02-P4（p4-reload）：persisted 事件只刷预算、全量重拉收敛到轮末。master/子开关任一 off 即回旧链。 */
function isNeed02P4ReloadOn(): boolean {
  try {
    const p4 = String(window.localStorage.getItem('halo-need02-p4') || '').toLowerCase();
    if (p4 === 'off') return false;
    const sub = String(window.localStorage.getItem('halo-need02-p4-reload') || '').toLowerCase();
    if (!sub) return true;
    return sub !== 'off';
  } catch {
    return true;
  }
}

// need23-R4：全量重拉合并窗口。同一 session 在 400ms 内的多次"需重拉"请求只执行最后一次，
// 且 "finish" 优先级高于 "persisted"（保证收尾那次一定发生）；finishTurn 走 reloadNow 立即拉并取消待定定时器。
const RELOAD_DEBOUNCE_MS = 400;
const pendingReload = new Map<string, { reason: 'persisted' | 'finish'; timer: number }>();

const reloadPriority = (r: string): number => (r === 'finish' ? 2 : 1);

async function doReload(sid: string, reason: string): Promise<void> {
  try {
    if (sid !== activeSessionId.value) return;
    if (reason !== 'persisted' && reason !== 'finish') {
      await agentRuntime().refreshBudget();
      return;
    }
    await agentRuntime().loadSessionMessages(sid);
    await agentRuntime().refreshBudget();
  } catch (err) {
    console.error('[agent-turn] reload failed:', err);
  }
}

function scheduleReload(sid: string, reason: 'persisted' | 'finish'): void {
  const cur = pendingReload.get(sid);
  const nextReason: 'persisted' | 'finish' = cur && reloadPriority(cur.reason) > reloadPriority(reason) ? cur.reason : reason;
  if (cur) window.clearTimeout(cur.timer);
  const timer = window.setTimeout(() => {
    pendingReload.delete(sid);
    void doReload(sid, nextReason);
  }, RELOAD_DEBOUNCE_MS);
  pendingReload.set(sid, { reason: nextReason, timer });
}

/** 立即重拉（取消该 session 的待定定时器），供 finishTurn 在清空容器前保证内容已就位。 */
async function reloadNow(sid: string): Promise<void> {
  const cur = pendingReload.get(sid);
  if (cur) {
    window.clearTimeout(cur.timer);
    pendingReload.delete(sid);
  }
  await doReload(sid, 'finish');
}

// need04-03：分流刷新：tool-completed 只内存更新（调用方已标脏）；persisted/finish 才拉全量对账（need23-R4 起走合并窗口）
// need02-P4（p4-reload）：persisted 只刷预算（400ms 防抖保留在 scheduleReload 的 budget 位），
// 全量 getSessionMessages 收敛到轮末 finish（Completed/Cancelled/Error/Truncated 经 finishTurn.reloadNow）。
// 首 user 已有 P1 乐观覆盖，无需中途全量拉。master/子开关 off 即回旧链。
const pendingBudget = new Map<string, number>();
function scheduleBudgetOnly(sid: string): void {
  const cur = pendingBudget.get(sid);
  if (cur) window.clearTimeout(cur);
  const timer = window.setTimeout(() => {
    pendingBudget.delete(sid);
    void agentRuntime()
      .refreshBudget()
      .catch((err) => console.error('[agent-turn] budget refresh failed:', err));
  }, RELOAD_DEBOUNCE_MS);
  pendingBudget.set(sid, timer);
}
export async function refreshAfterTool(targetSessionId?: string, reason?: string): Promise<void> {
  try {
    const sid = (targetSessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    // 后台会话只落桶、不碰当前视图：避免把 B 的消息列表刷成 A 的（切卡时 selectSession 天然对账）
    if (sid !== activeSessionId.value) return;
    if (reason === 'persisted' && isNeed02P4ReloadOn()) {
      scheduleBudgetOnly(sid);
      return;
    }
    if (reason !== 'persisted' && reason !== 'finish') {
      await agentRuntime().refreshBudget();
      return;
    }
    scheduleReload(sid, reason as 'persisted' | 'finish');
  } catch (err) {
    console.error('[agent-turn] refreshAfterTool failed:', err);
  }
}

export interface FinishTurnOptions {
  keepOnFailure?: boolean;
  deferClearMs?: number;
  terminalStatus?: 'completed' | 'cancelled' | 'error' | 'truncated';
  userMessagePersisted?: boolean | null;
  timelineFinalized?: boolean | null;
}

/** 有界对账重试间隔：重拉可能早于后端终态提交，退避补拉最多 3 次。 */
const PENDING_RECONCILE_DELAYS_MS = [200, 600, 1500];
const pendingReconcileTimers = new Map<string, number>();

/** 取消旧轮对账重试：新发送/重发/删除 pending 后旧定时器不得再触新轮。 */
export function cancelPendingReconcile(sessionId: string): void {
  const sid = (sessionId || '').trim();
  if (!sid) return;
  const timer = pendingReconcileTimers.get(sid);
  if (timer === undefined) return;
  pendingReconcileTimers.delete(sid);
  try {
    window.clearTimeout(timer);
  } catch {
    /* ignore */
  }
}

/** 摘要确认：同一 requestId 的轮次摘要是否已在当前视图就位；异常一律按未命中处理。 */
function hasSummaryConfirmed(sid: string, requestId: string): boolean {
  const rid = String(requestId || '').trim();
  if (!sid || !rid) return false;
  try {
    return agentRuntime().hasTurnSummary(sid, rid) === true;
  } catch {
    return false;
  }
}

function schedulePendingReconcile(sid: string, requestId: string, finishMark: number, attempt = 0): void {
  if (!sid || !requestId) return;
  if (attempt >= PENDING_RECONCILE_DELAYS_MS.length) {
    try {
      const cur = getTurn(sid)?.pendingUser;
      if (cur && String(cur.requestId || '') === requestId && (cur.status === 'cancelled' || cur.status === 'sending')) {
        console.warn('[agent-turn] pending reconcile exhausted, keep pending for manual resend:', {
          sessionId: sid,
          requestId,
        });
        console.debug('[agent-turn] pending reconcile exhausted, keep pending:', { requestId });
      }
    } catch {
      /* ignore */
    }
    return;
  }
  cancelPendingReconcile(sid);
  if (sid !== activeSessionId.value) return;
  if ((turnStartMarks[sid] || 0) !== finishMark) return;
  const delay = PENDING_RECONCILE_DELAYS_MS[attempt];
  try {
    const timer = window.setTimeout(() => {
      pendingReconcileTimers.delete(sid);
      void runPendingReconcile(sid, requestId, finishMark, attempt);
    }, delay);
    pendingReconcileTimers.set(sid, timer);
  } catch {
    /* ignore */
  }
}

async function runPendingReconcile(sid: string, requestId: string, finishMark: number, attempt: number): Promise<void> {
  try {
    if (!sid || !requestId) return;
    if (sid !== activeSessionId.value) return;
    if ((turnStartMarks[sid] || 0) !== finishMark) return;
    const cur = getTurn(sid)?.pendingUser;
    if (!cur || String(cur.requestId || '') !== requestId) return;
    if (cur.status !== 'cancelled' && cur.status !== 'sending') return;
    try {
      await agentRuntime().loadSessionMessages(sid, { background: true });
    } catch (err) {
      console.warn('[agent-turn] pending reconcile reload failed:', err);
    }
    if (sid !== activeSessionId.value) return;
    if ((turnStartMarks[sid] || 0) !== finishMark) return;
    const after = getTurn(sid)?.pendingUser;
    if (!after || String(after.requestId || '') !== requestId) return;
    if (after.status !== 'cancelled' && after.status !== 'sending') return;
    if (hasSummaryConfirmed(sid, requestId)) {
      const latest = getTurn(sid);
      if (
        latest &&
        latest.pendingUser &&
        String(latest.pendingUser.requestId || '') === requestId &&
        (turnStartMarks[sid] || 0) === finishMark
      ) {
        cancelPendingReconcile(sid);
        latest.pendingUser = null;
        touch(latest);
        try {
          console.debug('[agent-turn] pending drop by reconcile:', { requestId });
        } catch {
          /* ignore */
        }
      }
      return;
    }
    schedulePendingReconcile(sid, requestId, finishMark, attempt + 1);
  } catch {
    /* 对账永不阻断 */
  }
}

export async function finishTurn(targetSessionId?: string, opts?: FinishTurnOptions): Promise<void> {
  const sid = (targetSessionId ?? activeSessionId.value).trim();
  if (!sid) return;
  const t = ensureTurn(sid);
  const finishMark = turnStartMarks[sid] || 0;
  const finishPendingId = String(t.pendingUser?.requestId || '');
  const terminalStatus = opts?.terminalStatus ?? 'completed';
  const persistedOpt = opts?.userMessagePersisted;
  const userMessagePersisted = typeof persistedOpt === 'boolean' ? persistedOpt : null;
  try {
    flushSegBufSync(sid);
  } catch (e) {
    console.warn('[agent-turn] finish flush failed:', e);
  }
  try {
    finalizePending(t);
  } catch {
    /* optimistic tag finalize never blocks turn end */
  }
  try {
    const tags: Record<string, string> = {};
    for (const s of t.segs) {
      if (s.blockId && s.tag) tags[s.blockId] = String(s.tag);
      else if (s.key && s.tag) tags[s.key] = String(s.tag);
    }
    lastTurnSnapshot.value[sid] = {
      toolCards: [...t.toolCards],
      reasoning: t.streamingReasoning,
      text: t.streamingText,
      at: Date.now(),
      tags,
    };
    haloEventBus.emitLocal('agent:turn-archived', { sessionId: sid, snapshot: lastTurnSnapshot.value[sid] });
  } catch (e) {
    console.warn('[agent-turn] snapshot failed:', e);
  }
  // need23-R4：先立即重拉（取消该 session 待定的防抖定时器），保证内容已在视图里，
  // 再清空流式容器 —— 避免"内容先消失、400ms 后才回来"的闪烁。
  // need02-P3：defer 窗的 liveCompleting 必须在 reloadNow 之前立——handleTurnLifecycle 已先熄 isGenerating，
  // 若等 reload 完成才立，中间一轮 bridge 往返里内嵌 LiveBody 无依据渲染，仍是一帧真空。
  const deferMs = Math.max(0, Number(opts?.deferClearMs) || 0);
  const willDefer = deferMs > 0 && !(opts?.keepOnFailure && t.lastErrorInfo?.retry);
  if (willDefer) {
    t.liveCompleting = true;
    // requestId 会在下方清空；交接窗的对账摘要匹配必须使用这里冻结的快照。
    t.handoffRequestId = String(t.requestId || finishPendingId || '');
    touch(t);
    need02Debug('handoff', { requestId: t.requestId, deferMs, at: Date.now() });
  }
  dirtyPersisted.value[sid] = false;
  await reloadNow(sid);
  // 落盘对账完成后才允许稳态块上屏：只对进入交接窗的轮次置就绪。
  // 失败重试 / 取消路径没有交接窗，必须保持未就绪，由常规路径直接渲染稳态。
  if (willDefer) {
    t.steadyReady = true;
    touch(t);
  }
  // 轮末收口：只有同一 requestId 的摘要确认存在才清 pending，避免首回复前取消被无声丢失。
  // 可重试失败保留失败态供重发；旧定时器不得清理新轮待发送态；各会话只动自家分桶。
  try {
    if ((turnStartMarks[sid] || 0) === finishMark) {
      const cur = t.pendingUser;
      if (cur && (!finishPendingId || String(cur.requestId || '') === finishPendingId)) {
        const keep = !!(opts?.keepOnFailure && t.lastErrorInfo?.retry);
        if (!keep) {
          if (userMessagePersisted === false) {
            cancelPendingReconcile(sid);
            t.pendingUser = null;
            touch(t);
            try {
              console.debug('[agent-turn] pending drop unpersisted:', { requestId: finishPendingId });
            } catch {
              /* ignore */
            }
          } else if (finishPendingId && hasSummaryConfirmed(sid, finishPendingId)) {
            cancelPendingReconcile(sid);
            t.pendingUser = null;
            touch(t);
            try {
              console.debug('[agent-turn] pending drop finish:', { requestId: finishPendingId });
            } catch {
              /* ignore */
            }
          } else if (terminalStatus === 'cancelled') {
            cur.status = 'cancelled';
            cur.errorCode = cur.errorCode || 'TURN_CANCELLED';
            touch(t);
            try {
              console.debug('[agent-turn] pending cancelled keep:', { requestId: finishPendingId });
            } catch {
              /* ignore */
            }
            schedulePendingReconcile(sid, finishPendingId, finishMark, 0);
          } else if (terminalStatus === 'error') {
            // 错误终态沿用失败保留语义：不静默删除，等待重发或下一次对账。
            cur.status = 'failed';
            cur.errorCode = cur.errorCode || 'TURN_ERROR';
            touch(t);
          } else if (terminalStatus === 'completed' || terminalStatus === 'truncated') {
            // 完成/截断仍未命中摘要：记录诊断但不静默删除，避免与取消路径同因。
            try {
              console.warn('[agent-turn] finish without summary hit, keep pending for reconcile:', {
                sessionId: sid,
                requestId: finishPendingId,
                terminalStatus,
              });
              console.debug('[agent-turn] pending miss keep:', { requestId: finishPendingId, terminalStatus });
            } catch {
              /* ignore */
            }
            if (finishPendingId) schedulePendingReconcile(sid, finishPendingId, finishMark, 0);
          } else {
            try {
              console.warn('[agent-turn] finish without summary hit, keep pending:', {
                sessionId: sid,
                requestId: finishPendingId,
                terminalStatus,
              });
            } catch {
              /* ignore */
            }
          }
        }
      }
    }
  } catch {
    /* 清理永不阻断轮末 */
  }
  try {
    agentRuntime().reconcileTimes();
  } catch (e) {
    console.warn('[agent-turn] reconcile failed:', e);
  }
  t.requestId = '';
  if (opts?.keepOnFailure && t.lastErrorInfo?.retry) {
    touch(t);
    return;
  }
  // 终态已就位（reloadNow 先拉后清），流容器延迟 300ms 再清，供交接编排做交叉淡入淡出；
  // Cancel/Error 路径不延迟（调用方只在 Completed/Truncated 传 defer）。
  // 交接窗内 LiveBody 在 completing 期继续渲染全量 segs（absolute 脱流淡出），与稳态交叉淡入。
  // 交接埋点（DEV 限定，供量化）：handoff {requestId, liveMs, steadyMs}。
  const doClear = (): void => {
    t.toolCards = [];
    t.graduatedCards = [];
    t.toolCardIndex = {};
    t.segs = [];
    t.curIteration = 0;
    t.streamingText = '';
    t.streamingReasoning = '';
    t.streamingActive = false;
    t.liveCompleting = false;
    t.steadyReady = false;
    t.handoffRequestId = '';
    t.pendingTagOverrides = {};
    dropSegBuf(sid);
    touch(t);
  };
  if (willDefer) {
    // 交接窗竞态守卫：若窗内起了新轮（sendPrompt 重置 turnStartMarks），旧定时器不得清空新轮容器。
    const deferMark = turnStartMarks[sid] || 0;
    window.setTimeout(() => {
      try {
        if ((turnStartMarks[sid] || 0) !== deferMark) return;
        doClear();
      } catch (e) {
        console.warn('[agent-turn] deferred clear failed:', e);
      }
    }, deferMs);
    return;
  }
  doClear();
}

/** llm:stream-event StreamError 分支（旧 ensureListeners 内同分支语义）。 */
export function applyStreamError(evt: SessionStreamEventDto): void {
  if (!evt || evt.kind !== 'StreamError') return;
  const sid = resolveTargetSession(evt.sessionId);
  if (!sid) return;
  const t = ensureTurn(sid);
  flushSegBufSync(sid);
  t.isGenerating = false;
  t.streamingActive = false;
  t.lastError = evt.errorMessage || evt.errorCode || '流式中断';
  t.lastErrorInfo = { code: evt.errorCode || 'STREAM_ERROR', message: t.lastError, retry: true };
  t.turnNotice = `流式中断：${t.lastError}（可点“重试本轮”）`;
  touch(t);
  toast('error', t.lastError);
}

/** agent:turn-event 生命周期分支（旧 handleTurnEvent 内转归约分支；工具/子代理分支归 cards，Iteration 归 stream）。 */
export function handleTurnLifecycle(kind: string, payload: Record<string, unknown>, sessionId?: string): void {
  const sid = (sessionId ?? activeSessionId.value).trim();
  if (!sid) return;
  const t = ensureTurn(sid);
  const num = (v: unknown): number => (typeof v === 'number' ? v : Number(v) || 0);
  const str = (v: unknown): string => (v === null || v === undefined ? '' : String(v));
  switch (kind) {
    case 'AssistantMessagePersisted': {
      flushSegBufSync(sid);
      const pit = num(payload['iteration']);
      // need23-R7：只让正文/思考段毕业；工具段等 ToolMessagePersisted 逐条毕业，
      // 否则工具会同时从活动容器与稳态消失（执行期完全不可见）。
      if (pit > 0) markIterationTextPersisted(t, pit);
      else if (t.curIteration > 0) markIterationTextPersisted(t, t.curIteration);
      t.telemetry.inputTokens = num(payload['inputTokens']);
      t.telemetry.outputTokens = num(payload['outputTokens']);
      t.telemetry.cachedTokens = num(payload['cachedTokens']);
      t.telemetry.billedPrompt = num(payload['billedPrompt']);
      t.telemetry.billedCompletion = num(payload['billedCompletion']);
      t.telemetry.liveEst = 0;
      touch(t);
      touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
      void refreshAfterTool(sid, 'persisted');
      break;
    }
    case 'TurnCompleted': {
      flushSegBufSync(sid);
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
      // 主轮终态兜底：残留 running 按失败收敛，绝不覆盖兄弟子真实终态。
      finalizeSubAgents(t, 'failed');
      touch(t);
      touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
      void finishTurn(sid, { deferClearMs: 300, terminalStatus: 'completed' });
      agentRuntime().generateTitle(sid);
      break;
    }
    case 'CompactStarted': {
      t.compacting = true;
      t.isGenerating = true;
      t.streamingActive = false;
      t.turnNotice = '压缩中…不可中断';
      touch(t);
      break;
    }
    case 'CompactCompleted': {
      t.compacting = false;
      t.isGenerating = false;
      t.streamingActive = false;
      t.turnNotice =
        num(payload['summaryTokens']) > 0
          ? `✔ 已压缩：${num(payload['removedMessages'])}条收敛为摘要（摘要 ≈${num(payload['summaryTokens'])} tokens）`
          : `✔ 已压缩：${num(payload['removedMessages'])}条收敛为摘要`;
      const metadataWarning = str(payload['metadataWarning']);
      if (metadataWarning) {
        toast('warning', `压缩已提交，但元数据刷新告警：${metadataWarning}`);
      }
      touch(t);
      touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
      void refreshAfterTool(sid, 'finish');
      break;
    }
    case 'CompactNoOp': {
      t.compacting = false;
      t.isGenerating = false;
      t.streamingActive = false;
      t.turnNotice = `无需压缩：${noOpReasonText(str(payload['noOpReason']))}`;
      touch(t);
      touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
      void refreshAfterTool(sid);
      break;
    }
    case 'CompactFailed': {
      t.compacting = false;
      t.isGenerating = false;
      t.streamingActive = false;
      t.lastError = str(payload['errorMessage']) || str(payload['errorCode']) || '压缩失败';
      t.lastErrorInfo = { code: str(payload['errorCode']) || 'COMPACT_FAILED', message: t.lastError, retry: false };
      t.turnNotice = `压缩失败：${t.lastError}`;
      toast('error', t.lastError);
      touch(t);
      break;
    }
    case 'TurnTruncated': {
      flushSegBufSync(sid);
      t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
      t.isGenerating = false;
      t.streamingActive = false;
      // need23-R6：文案按后端回传的 maxIterations / reason 渲染（不再硬编码 25；无限轮次不显示数字）。
      const maxIt = num(payload['maxIterations']);
      const truncReason = str(payload['reason']);
      const truncCode = str(payload['errorCode']);
      if (truncReason === 'repeated-tool-error') {
        t.turnNotice = `工具调用连续失败（${truncCode || '错误'}），已暂停自动执行。发送“继续”以恢复任务。`;
      } else if (maxIt > 0) {
        t.turnNotice = `已达到单轮最大迭代次数（${maxIt}），已暂停自动执行。请检查中间结果后发送“继续”以恢复任务。`;
      } else {
        t.turnNotice = '已达到单轮迭代上限，已暂停自动执行。请检查中间结果后发送“继续”以恢复任务。';
      }
      touch(t);
      void finishTurn(sid, { deferClearMs: 300, terminalStatus: 'truncated' });
      agentRuntime().generateTitle(sid);
      break;
    }
    case 'TurnCancelled': {
      flushSegBufSync(sid);
      t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
      t.isGenerating = false;
      t.streamingActive = false;
      t.turnNotice = '已停止生成。';
      // 取消掐断整树：仍 running 的子收敛为 cancelled，已有终态的兄弟子不变。
      finalizeSubAgents(t, 'cancelled');
      touch(t);
      void finishTurn(sid, {
        terminalStatus: 'cancelled',
        userMessagePersisted: typeof payload['userMessagePersisted'] === 'boolean' ? (payload['userMessagePersisted'] as boolean) : null,
        timelineFinalized: typeof payload['timelineFinalized'] === 'boolean' ? (payload['timelineFinalized'] as boolean) : null,
      });
      break;
    }
    case 'TurnError': {
      flushSegBufSync(sid);
      t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
      t.isGenerating = false;
      t.streamingActive = false;
      t.lastError = str(payload['errorMessage']) || str(payload['errorCode']) || '推理失败';
      // need22-P2 F5：结构化错误+保留流式卡（finishTurn(keepOnFailure) 不清容器）+重试
      t.lastErrorInfo = { code: str(payload['errorCode']) || 'TURN_ERROR', message: t.lastError, retry: true };
      // need22-P3 G6：TurnError 同落 turnNotice（模板已有 turnNotice + 重试按钮）
      t.turnNotice = `推理失败：${t.lastError}（可点“重试本轮”）`;
      // 错误收尾：仍 running 的子收敛为 failed，已有终态的兄弟子不变。
      finalizeSubAgents(t, 'failed');
      touch(t);
      toast('error', t.lastError);
      void finishTurn(sid, { keepOnFailure: true, terminalStatus: 'error' });
      agentRuntime().generateTitle(sid);
      break;
    }
    case 'BlockTagUpdated': {
      const bid = str(payload['blockId']);
      const tag = str(payload['tag']);
      const reason = str(payload['reason']);
      if (reason !== 'demote' && reason !== 'finalize' && reason !== 'flip') break;
      if (tag !== 'process' && tag !== 'final') break;
      if (!bid) break;
      // 先把 rAF 缓冲里的 delta 建成 seg，避免 flip/finalize 事件打空后旧 final 残留。
      flushSegBufSync(sid);
      let touched = false;
      for (const s of t.segs) {
        if (String(s.blockId || '') === bid || String(s.key || '') === bid) {
          if (tag === 'final') markSoleFinal(t, s);
          else s.tag = tag;
          touched = true;
        }
      }
      if (!touched) {
        // 事件先于 delta 到达：记轻量 override，seg 建成时消费；后到事件覆盖旧值。
        t.pendingTagOverrides[bid] = { tag, reason: reason as 'demote' | 'finalize' | 'flip' };
      }
      touch(t);
      break;
    }
    default:
      break;
  }
}

function noOpReasonText(reason: string): string {
  switch (reason) {
    case 'EMPTY':
      return '会话为空';
    case 'TOO_SHORT':
      return '历史还太短';
    case 'NO_NEW_CONTENT':
      return '桩后没有新内容';
    default:
      return '当前无需压缩';
  }
}

export function isCompacting(sessionId: string): boolean {
  const t = getTurn(sessionId);
  return t?.compacting === true;
}

export function isBusyForHistory(sessionId: string): boolean {
  const t = getTurn(sessionId);
  if (!!t && (t.isGenerating || t.streamingActive || t.compacting)) return true;
  // need09-06：问询 pending 视为忙（重放/分支/压缩/新发送一律拒绝，请先回答问题）
  try {
    return agentRuntime().askPending(sessionId);
  } catch {
    return false;
  }
}

/** 等指定会话 TurnCancelled 事件；超时回 false（不抛）。 */
function waitTurnCancelled(sessionId: string, timeoutMs: number): Promise<boolean> {
  return new Promise<boolean>((resolve) => {
    let done = false;
    const finish = (v: boolean): void => {
      if (done) return;
      done = true;
      try {
        unsub();
      } catch {
        /* ignore */
      }
      resolve(v);
    };
    const timer = window.setTimeout(() => finish(false), Math.max(500, timeoutMs));
    void timer;
    const unsub = haloEventBus.on<{ sessionId: string; kind: string }>('agent:turn-event', (evt) => {
      try {
        if (!evt || evt.kind !== 'TurnCancelled') return;
        if (String(evt.sessionId || '').trim() !== sessionId) return;
        finish(true);
      } catch {
        /* ignore */
      }
    });
  });
}

export async function stopGenerating(targetSessionId?: string): Promise<void> {
  const sessionId = (targetSessionId ?? activeSessionId.value).trim();
  if (!sessionId) return;
  if (isCompacting(sessionId)) return; // need05-03：压缩中不可中断，中断按钮无效
  // 等后端 TurnCancelled 事件后再清本地（超时 5s 降级保留 cancelled + 对账重试）
  const cancelled = waitTurnCancelled(sessionId, 5000);
  try {
    const bridge = getHostBridge();
    await bridge.cancelAgentTurn(sessionId);
  } catch (err) {
    console.error('[agent-turn] stopGenerating failed:', err);
  }
  const got = await cancelled;
  const t = ensureTurn(sessionId);
  if (!got) {
    console.warn('[agent-turn] stopGenerating: TurnCancelled 超时 5s，降级本地保留取消态');
    toast('warning', '取消指令已发送，正在确认历史状态…');
    // 超时不删 pending：user 已进入上下文的可能性更高，标 cancelled 并安排对账重试。
    const pendingId = String(t.pendingUser?.requestId || t.requestId || '').trim();
    if (pendingId && t.pendingUser && String(t.pendingUser.requestId || '') === pendingId) {
      t.pendingUser.status = 'cancelled';
      t.pendingUser.errorCode = t.pendingUser.errorCode || 'CANCEL_CONFIRM_TIMEOUT';
    }
    t.isGenerating = false;
    t.streamingActive = false;
    t.turnNotice = '取消指令已发送，正在确认历史状态…';
    touch(t);
    try {
      await agentRuntime().loadSessionMessages(sessionId, { background: true });
    } catch {
      /* 重拉失败走对账重试 */
    }
    if (pendingId) schedulePendingReconcile(sessionId, pendingId, turnStartMarks[sessionId] || 0, 0);
    return;
  }
  // need09-06：问询等待期间停止 = 取消提问 + 取消整轮（后端 ct 联动转 Fail）
  try {
    if (agentRuntime().askPending(sessionId)) agentRuntime().clearAskPending(sessionId);
  } catch {
    /* ignore */
  }
  const idle = ensureTurn(sessionId);
  idle.isGenerating = false;
  idle.streamingActive = false;
  idle.requestId = '';
  idle.turnNotice = '已停止生成。';
  touch(idle);
  await refreshAfterTool(sessionId);
}
