/** need21-F03：turn 生命周期事件归约（自旧 agentStore handleTurnEvent/finishTurn 原样搬入）。
 * 后端回执分支（Persisted/Completed/Cancelled/Error/Truncated/Compact/StreamError）唯一归约点；
 * workspace/session 侧效经 runtime 上下文（旧转发注入实现），本模块不直 import 其他 store。
 */
import { getHostBridge, haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import {
  activeSessionId,
  dirtyPersisted,
  ensureTurn,
  getTurn,
  lastTurnSnapshot,
  markIterationTextPersisted,
  resolveTargetSession,
  toast,
  touch,
  turnStartMarks,
} from './model';
import { agentRuntime } from './runtime';
import { dropSegBuf, flushSegBufSync } from './useAgentStream';

/** need04-01：任何写消息路径结束时内存态秒级刷新会话卡时间（乐观+事件双通道）。 */
function touchSessionClock(sessionId: string, iso?: string): void {
  try {
    agentRuntime().touchSessionTime(sessionId, iso);
  } catch {
    /* 时间刷新永不阻断主链路 */
  }
}

/** need02-P2：读 p2 开关（turnEvents 不直连 need02Flags 组合式，照 kill switch 写法直读 localStorage）。 */
function isNeed02P2On(): boolean {
  try {
    const v = String(window.localStorage.getItem('halo-need02-p2') || '').toLowerCase();
    if (!v) return true;
    return v !== 'off';
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
export async function refreshAfterTool(targetSessionId?: string, reason?: string): Promise<void> {
  try {
    const sid = (targetSessionId ?? activeSessionId.value).trim();
    if (!sid) return;
    // 后台会话只落桶、不碰当前视图：避免把 B 的消息列表刷成 A 的（切卡时 selectSession 天然对账）
    if (sid !== activeSessionId.value) return;
    if (reason !== 'persisted' && reason !== 'finish') {
      await agentRuntime().refreshBudget();
      return;
    }
    scheduleReload(sid, reason as 'persisted' | 'finish');
  } catch (err) {
    console.error('[agent-turn] refreshAfterTool failed:', err);
  }
}

export async function finishTurn(targetSessionId?: string, opts?: { keepOnFailure?: boolean; deferClearMs?: number }): Promise<void> {
  const sid = (targetSessionId ?? activeSessionId.value).trim();
  if (!sid) return;
  const t = ensureTurn(sid);
  try {
    flushSegBufSync(sid);
  } catch (e) {
    console.warn('[agent-turn] finish flush failed:', e);
  }
  try {
    lastTurnSnapshot.value[sid] = { toolCards: [...t.toolCards], reasoning: t.streamingReasoning, text: t.streamingText, at: Date.now() };
    haloEventBus.emitLocal('agent:turn-archived', { sessionId: sid, snapshot: lastTurnSnapshot.value[sid] });
  } catch (e) {
    console.warn('[agent-turn] snapshot failed:', e);
  }
  // need23-R4：先立即重拉（取消该 session 待定的防抖定时器），保证内容已在视图里，
  // 再清空流式容器 —— 避免"内容先消失、400ms 后才回来"的闪烁。
  dirtyPersisted.value[sid] = false;
  await reloadNow(sid);
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
  // need02-P2：终态已就位（reloadNow 先拉后清），流容器延迟 300ms 再清，供 P3 交接编排做交叉淡入淡出；
  // p2 off 时保持立即清（旧行为）。Cancel/Error 路径不延迟（调用方只在 Completed/Truncated 传 defer）。
  const deferMs = Math.max(0, Number(opts?.deferClearMs) || 0);
  const doClear = (): void => {
    t.toolCards = [];
    t.toolCardIndex = {};
    t.segs = [];
    t.curIteration = 0;
    t.streamingText = '';
    t.streamingReasoning = '';
    t.streamingActive = false;
    dropSegBuf(sid);
    touch(t);
  };
  if (deferMs > 0) {
    touch(t);
    window.setTimeout(() => {
      try {
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
      // need13：完成即解除子等待
      t.subAgents.waiting = false;
      t.subAgents.done = t.subAgents.total;
      touch(t);
      touchSessionClock(sid, str(payload['updatedAtUtc']) || undefined);
      void finishTurn(sid, isNeed02P2On() ? { deferClearMs: 300 } : undefined);
      agentRuntime().generateTitle(sid);
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
      // need22-P2 F5：结构化错误（可重试=false，压缩失败走按钮重发）
      t.lastErrorInfo = { code: str(payload['errorCode']) || 'COMPACT_FAILED', message: t.lastError, retry: false };
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
      void finishTurn(sid, isNeed02P2On() ? { deferClearMs: 300 } : undefined);
      agentRuntime().generateTitle(sid);
      break;
    }
    case 'TurnCancelled': {
      flushSegBufSync(sid);
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
      flushSegBufSync(sid);
      t.telemetry.totalMs = turnStartMarks[sid] > 0 ? Date.now() - turnStartMarks[sid] : null;
      t.isGenerating = false;
      t.streamingActive = false;
      t.lastError = str(payload['errorMessage']) || str(payload['errorCode']) || '推理失败';
      // need22-P2 F5：结构化错误+保留流式卡（finishTurn(keepOnFailure) 不清容器）+重试
      t.lastErrorInfo = { code: str(payload['errorCode']) || 'TURN_ERROR', message: t.lastError, retry: true };
      // need22-P3 G6：TurnError 同落 turnNotice（模板已有 turnNotice + 重试按钮）
      t.turnNotice = `推理失败：${t.lastError}（可点“重试本轮”）`;
      // need13：错误即解除子等待
      t.subAgents.waiting = false;
      touch(t);
      toast('error', t.lastError);
      void finishTurn(sid, { keepOnFailure: true });
      agentRuntime().generateTitle(sid);
      break;
    }
    default:
      break;
  }
}

export function isCompacting(sessionId: string): boolean {
  const t = getTurn(sessionId);
  return !!t && t.isGenerating && t.turnNotice.includes('压缩中');
}

export function isBusyForHistory(sessionId: string): boolean {
  const t = getTurn(sessionId);
  if (!!t && (t.isGenerating || t.streamingActive || t.turnNotice.includes('压缩中'))) return true;
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
  // need22-P2 F5：等后端 TurnCancelled 事件后再清本地（超时 5s 降级清 + 告警）
  const cancelled = waitTurnCancelled(sessionId, 5000);
  try {
    const bridge = getHostBridge();
    await bridge.cancelAgentTurn(sessionId);
  } catch (err) {
    console.error('[agent-turn] stopGenerating failed:', err);
  }
  const got = await cancelled;
  if (!got) {
    console.warn('[agent-turn] stopGenerating: TurnCancelled 超时 5s，降级本地清理');
    toast('warning', '取消指令已发送，但后端确认超时，已本地清理');
  }
  // need09-06：问询等待期间停止 = 取消提问 + 取消整轮（后端 ct 联动转 Fail）
  try {
    if (agentRuntime().askPending(sessionId)) agentRuntime().clearAskPending(sessionId);
  } catch {
    /* ignore */
  }
  const t = ensureTurn(sessionId);
  t.isGenerating = false;
  t.streamingActive = false;
  t.requestId = '';
  t.turnNotice = '已停止生成。';
  touch(t);
  await refreshAfterTool(sessionId);
}
