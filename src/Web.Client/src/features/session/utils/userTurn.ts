// need02-P2：用户轮次聚合纯函数（替代 need-01 的 iteration 粒度，D1=B：上折叠行 + 下末段直出）。
// 只读 messages，新建 UserTurn/Step/entry，绝不就地修改传入对象（05 R1）。
// 字数口径：String.length（UTF-16），emoji 计 2，含 markdown 源码；0 字隐藏（2026-10-26 已定）。
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TurnGroup } from './turnGroup';
import { turnPhaseOf, type TurnPhase } from './turnProcess';

/** 失败工具判定口径（与 turnRun.TOOL_ERROR_MARK / useTimeline.failedToolCount 同源）。 */
export const USER_TURN_TOOL_ERROR_MARK = '[Tool Error';

/** 用户轮次内按 assistant 切分的单步（iteration 序）。 */
export interface UserTurnStep {
  iteration: number;
  assistantId: string;
  content: string;
  reasoning: string | null;
  toolMessages: UnifiedChatMessage[];
  toolCalls: Array<{ id: string; name: string }>;
  telemetry: TurnGroup['telemetry'];
  createdAtUtc: string;
}

export interface UserTurn {
  /** 稳定键：`uturn-<首userId>`；pending 期为 `uturn-pending-<requestId>`，转正后保持 pending key 不变（05 R3）。 */
  key: string;
  userId: string;
  userMsg: UnifiedChatMessage | null;
  pending: boolean;
  requestId: string | null;
  steps: UserTurnStep[];
  stepCount: number;
  toolCount: number;
  failedCount: number;
  contentChars: number;
  reasoningChars: number;
  startedAtUtc: string;
  endedAtUtc: string;
  isRunning: boolean;
  phase: TurnPhase;
  phaseLabel: string;
  /** 显示用汇总（D1=B：耗时/工具/失败，不含字数；字数仅 fullSummary 供测试/展开区）。 */
  summary: string;
  /** 完整汇总（含字数，供测试断言与展开区使用）。 */
  fullSummary: string;
}

/** P2 时间轴条目：用户轮 + 系统；孤儿 assistant/tool 归入无 user 头的 UserTurn（不丢内容）。 */
export type UserTurnEntry = { key: string; kind: 'user-turn'; turn: UserTurn } | { key: string; kind: 'system'; msg: UnifiedChatMessage };

export interface BuildUserTurnsOpts {
  activeAssistantId?: string | null;
  isGenerating?: boolean;
  /** TurnCompleted.totalMs（运行轮耗时优先用它；稳态轮用 ended-started） */
  liveTotalMs?: number | null;
  now?: number;
  /** 重试/分支产生的新 user 自然另起一轮（D3=A），无需额外参数。 */
  stoppedTurnKeys?: ReadonlySet<string>;
}

// ---- pending key 稳定别名（转正后 key 保持 pending key 不变，05 R2–R3） ----
const confirmedAlias = new Map<string, string>();
/** workspace 对账命中时调用：confirmedId -> pendingKey。 */
export function rememberPendingAlias(confirmedId: string, pendingKey: string): void {
  const c = String(confirmedId || '').trim();
  const p = String(pendingKey || '').trim();
  if (!c || !p) return;
  confirmedAlias.set(c, p);
  if (confirmedAlias.size > 500) {
    const first = confirmedAlias.keys().next();
    if (!first.done) confirmedAlias.delete(first.value);
  }
}
export function pendingAliasOf(confirmedId: string): string | null {
  const c = String(confirmedId || '').trim();
  if (!c) return null;
  return confirmedAlias.get(c) || null;
}
export function clearPendingAlias(): void {
  confirmedAlias.clear();
}

function toolRefsOf(msg: UnifiedChatMessage): Array<{ id: string; name: string }> {
  const list = (msg as unknown as { toolCalls?: Array<{ id?: string; function?: { name?: string } }> }).toolCalls;
  if (!Array.isArray(list)) return [];
  return list
    .map((tc) => ({
      id: String((tc as { id?: string }).id || ''),
      name: String(tc.function?.name || (tc as { name?: string }).name || 'tool'),
    }))
    .filter((t) => t.id);
}

function failedOfContent(content: string | null | undefined): boolean {
  return String(content || '').includes(USER_TURN_TOOL_ERROR_MARK);
}

function isPendingUserId(id: string): boolean {
  return String(id || '').startsWith('pending-');
}

function turnKeyOfUser(userId: string): string {
  return `uturn-${String(userId || '')}`;
}

function durationLabel(startedIso: string, endedIso: string, now: number): string {
  const s = Date.parse(String(startedIso || ''));
  const e = Date.parse(String(endedIso || ''));
  let ms = NaN;
  if (Number.isFinite(s) && Number.isFinite(e) && e >= s) ms = e - s;
  else if (Number.isFinite(s)) ms = now - s;
  if (!Number.isFinite(ms) || ms < 0) return '';
  const sec = Math.round(ms / 1000);
  if (sec <= 0) return '';
  return `耗时${sec}s`;
}

function phaseIconOf(phase: TurnPhase): string {
  switch (phase) {
    case 'failed':
      return '❌';
    case 'stopped':
      return '⏹';
    case 'running':
      return '⏳';
    default:
      return '✅';
  }
}

function phaseWordOf(phase: TurnPhase): string {
  switch (phase) {
    case 'failed':
      return '失败';
    case 'stopped':
      return '已停止';
    case 'running':
      return '运行中';
    default:
      return '已完成';
  }
}

/** 显示汇总（D1=B：过程内容在组件前缀，字数不进折叠行，0 值隐藏）。 */
export function summaryOf(input: { duration: string; toolCount: number; failedCount: number }): string {
  const parts: string[] = [];
  if (input.duration) parts.push(input.duration);
  if (input.toolCount > 0) parts.push(`工具${input.toolCount}`);
  if (input.failedCount > 0) parts.push(`失败${input.failedCount}`);
  return parts.join(' · ');
}

/** 完整汇总（含字数，0 字隐藏；供测试/展开区）。 */
export function fullSummaryOf(input: {
  duration: string;
  toolCount: number;
  failedCount: number;
  contentChars: number;
  reasoningChars: number;
}): string {
  const parts: string[] = [];
  if (input.duration) parts.push(input.duration);
  if (input.toolCount > 0) parts.push(`工具${input.toolCount}`);
  if (input.failedCount > 0) parts.push(`失败${input.failedCount}`);
  if (input.contentChars > 0) parts.push(`正文${input.contentChars}字`);
  if (input.reasoningChars > 0) parts.push(`思考${input.reasoningChars}字`);
  return parts.join(' · ');
}

function makeStep(iteration: number, assistant: UnifiedChatMessage | null, tools: UnifiedChatMessage[]): UserTurnStep {
  const content = String(assistant?.content ?? '');
  const reasoning = (assistant?.reasoningContent as string | null | undefined) ?? null;
  const toolCalls = assistant ? toolRefsOf(assistant) : [];
  const telemetry =
    assistant && (assistant.inputTokens != null || assistant.outputTokens != null)
      ? {
          inputTokens: assistant.inputTokens ?? null,
          cachedTokens: assistant.cachedTokens ?? null,
          outputTokens: assistant.outputTokens ?? null,
        }
      : null;
  return {
    iteration,
    assistantId: String(assistant?.id || ''),
    content,
    reasoning: reasoning ? String(reasoning) : null,
    toolMessages: tools.slice(),
    toolCalls,
    telemetry,
    createdAtUtc: String(
      (assistant as unknown as { createdAtUtc?: string } | null)?.createdAtUtc || tools[tools.length - 1]?.createdAtUtc || ''
    ),
  };
}

function finalizeTurn(args: {
  userMsg: UnifiedChatMessage | null;
  orphanHeadId: string;
  steps: Array<{ assistant: UnifiedChatMessage | null; tools: UnifiedChatMessage[] }>;
  requestId: string | null;
  opts: BuildUserTurnsOpts;
  now: number;
}): UserTurn {
  const { userMsg, orphanHeadId, steps, requestId, opts, now } = args;
  const userId = userMsg ? String(userMsg.id || '') : orphanHeadId;
  let key = turnKeyOfUser(userId);
  // 转正稳定性：真实 id 若命中别名，沿用 pending key（DOM 不重建，05 R3）。
  if (userMsg && !isPendingUserId(userId)) {
    const alias = pendingAliasOf(userId);
    if (alias) key = alias.startsWith('uturn-') ? alias : turnKeyOfUser(alias.replace(/^pending-/, 'pending-'));
  }
  const built: UserTurnStep[] = steps.map((s, i) => makeStep(i + 1, s.assistant, s.tools));
  let toolCount = 0;
  let failedCount = 0;
  let contentChars = 0;
  let reasoningChars = 0;
  for (const st of built) {
    toolCount += st.toolMessages.length;
    for (const m of st.toolMessages) if (failedOfContent(m.content)) failedCount += 1;
    contentChars += String(st.content || '').length;
    reasoningChars += String(st.reasoning || '').length;
  }
  const startedAtUtc = String((userMsg as unknown as { createdAtUtc?: string } | null)?.createdAtUtc || built[0]?.createdAtUtc || '');
  const endedAtUtc = String(built[built.length - 1]?.createdAtUtc || startedAtUtc);
  // 运行态：不再猜末 assistant（05 A3），按“本轮含末条 live + 全局 generating”判定。
  const activeId = String(opts.activeAssistantId || '');
  const generating = opts.isGenerating === true;
  const containsActive = activeId ? built.some((s) => s.assistantId === activeId) : false;
  const lastStepActive = activeId ? built.length > 0 && built[built.length - 1]?.assistantId === activeId : false;
  // P0-2：只有「本轮仍在生成」的候选轮才允许 running。是否真是最后一轮由 buildUserTurns 收尾统一裁决
  // （它能看到全部轮次），此处只保证“没有 live 落点的轮绝不亮运行态”，避免旧轮抢先拿到 live 区。
  const isRunning =
    generating && (containsActive || (lastStepActive && built.length > 0) || (activeId === '' && built.length === 0 && !!userMsg));
  // 失败态：任一 tool 含 [Tool Error 即失败（与 failedToolCount 同源）。
  const stopped = opts.stoppedTurnKeys ? opts.stoppedTurnKeys.has(key) : false;
  const phase: TurnPhase = turnPhaseOf({ hasOwnError: failedCount > 0, stopped, isActive: isRunning, hasContent: contentChars > 0 });
  let duration = durationLabel(startedAtUtc, isRunning ? new Date(now).toISOString() : endedAtUtc, now);
  if (isRunning && opts.liveTotalMs == null) {
    // 运行中耗时按 started-now（durationLabel 已处理）
  }
  if (!isRunning && !duration && startedAtUtc && endedAtUtc) duration = durationLabel(startedAtUtc, endedAtUtc, now);
  const summary = summaryOf({ duration, toolCount, failedCount });
  const fullSummary = fullSummaryOf({ duration, toolCount, failedCount, contentChars, reasoningChars });
  const phaseLabel = duration ? `${phaseWordOf(phase)} · ${duration}` : phaseWordOf(phase);
  return {
    key,
    userId,
    userMsg,
    pending: userMsg ? isPendingUserId(String(userMsg.id || '')) : false,
    requestId,
    steps: built,
    stepCount: built.length,
    toolCount,
    failedCount,
    contentChars,
    reasoningChars,
    startedAtUtc,
    endedAtUtc,
    isRunning,
    phase,
    phaseLabel,
    summary,
    fullSummary,
  };
}

function requestIdOf(msg: UnifiedChatMessage | null): string | null {
  if (!msg) return null;
  const v = (msg as unknown as { requestId?: unknown }).requestId;
  const s = String(v ?? '').trim();
  return s ? s : null;
}

/**
 * 由 messages 聚合用户轮次（纯函数）。
 * - user（含乐观 pending-）断开上一轮，新起 UserTurn；
 * - assistant/tool 并入当前轮（每个 assistant 起一 step，tool 跟随其前 assistant；无 assistant 的 tool 自成一 step）；
 * - system 独立 entry，不断轮；
 * - 无 user 开局残留（老数据/compact）归入孤儿轮（userMsg=null，key 按首条 id）。
 */
export function buildUserTurns(messages: UnifiedChatMessage[] | null | undefined, opts: BuildUserTurnsOpts = {}): UserTurnEntry[] {
  const list = Array.isArray(messages) ? messages : [];
  const now = typeof opts.now === 'number' ? opts.now : Date.now();
  const out: UserTurnEntry[] = [];
  let curUser: UnifiedChatMessage | null = null;
  let curRequestId: string | null = null;
  let curSteps: Array<{ assistant: UnifiedChatMessage | null; tools: UnifiedChatMessage[] }> = [];
  let orphanSteps: Array<{ assistant: UnifiedChatMessage | null; tools: UnifiedChatMessage[] }> = [];
  let orphanHeadId = '';
  let pendingStepAssistant: UnifiedChatMessage | null = null;
  let pendingStepTools: UnifiedChatMessage[] = [];

  const pushStep = (target: Array<{ assistant: UnifiedChatMessage | null; tools: UnifiedChatMessage[] }>): void => {
    if (!pendingStepAssistant && pendingStepTools.length === 0) return;
    target.push({ assistant: pendingStepAssistant, tools: pendingStepTools });
    if (target === orphanSteps && !orphanHeadId) orphanHeadId = String(pendingStepAssistant?.id || pendingStepTools[0]?.id || '');
    pendingStepAssistant = null;
    pendingStepTools = [];
  };
  const emitTurn = (
    userMsg: UnifiedChatMessage | null,
    steps: Array<{ assistant: UnifiedChatMessage | null; tools: UnifiedChatMessage[] }>,
    requestId: string | null
  ): void => {
    if (!userMsg && steps.length === 0) return;
    const t = finalizeTurn({
      userMsg,
      orphanHeadId: userMsg ? String(userMsg.id || '') : orphanHeadId || `orphan-${out.length}`,
      steps,
      requestId,
      opts,
      now,
    });
    out.push({ key: t.key, kind: 'user-turn', turn: t });
  };
  const flushOrphans = (): void => {
    if (!orphanSteps.length) return;
    const t = finalizeTurn({
      userMsg: null,
      orphanHeadId: orphanHeadId || `orphan-${out.length}`,
      steps: orphanSteps,
      requestId: null,
      opts,
      now,
    });
    out.push({ key: t.key, kind: 'user-turn', turn: t });
    orphanSteps = [];
    orphanHeadId = '';
  };

  for (const msg of list) {
    const role = String((msg as UnifiedChatMessage)?.role || '').toLowerCase();
    if (role === 'user') {
      // 断开上一轮：先落步骤，再落整轮，最后落 ранее攒的孤儿（孤儿恒在更早位置，已先落）。
      pushStep(curUser ? curSteps : orphanSteps);
      if (curUser || curSteps.length) emitTurn(curUser, curSteps, curRequestId);
      else flushOrphans();
      flushOrphans();
      curUser = msg;
      curRequestId = requestIdOf(msg);
      curSteps = [];
      pendingStepAssistant = null;
      pendingStepTools = [];
      continue;
    }
    if (role === 'system') {
      // 系统独立 entry；已有实质步骤时先落整轮保证时序 [turn, system]，否则轮保持开放（不断轮）。
      if (curUser && curSteps.length === 0 && !pendingStepAssistant && pendingStepTools.length === 0) {
        out.push({ key: `s-${String(msg.id || `sys-${out.length}`)}`, kind: 'system', msg });
      } else if (curUser && (curSteps.length > 0 || pendingStepAssistant || pendingStepTools.length > 0)) {
        pushStep(curSteps);
        emitTurn(curUser, curSteps, curRequestId);
        curUser = null;
        curRequestId = null;
        curSteps = [];
        out.push({ key: `s-${String(msg.id || `sys-${out.length}`)}`, kind: 'system', msg });
      } else {
        pushStep(orphanSteps);
        flushOrphans();
        out.push({ key: `s-${String(msg.id || `sys-${out.length}`)}`, kind: 'system', msg });
      }
      continue;
    }
    if (role === 'assistant') {
      pushStep(curUser ? curSteps : orphanSteps);
      pendingStepAssistant = msg;
      pendingStepTools = [];
      continue;
    }
    if (role === 'tool') {
      pendingStepTools.push(msg);
      if (!curUser && !orphanHeadId && !pendingStepAssistant && pendingStepTools.length === 1) {
        orphanHeadId = String(msg.id || '');
      }
      continue;
    }
    out.push({
      key: `s-${String((msg as UnifiedChatMessage).id || `unknown-${out.length}`)}`,
      kind: 'system',
      msg: msg as UnifiedChatMessage,
    });
  }
  pushStep(curUser ? curSteps : orphanSteps);
  if (curUser || curSteps.length) emitTurn(curUser, curSteps, curRequestId);
  else flushOrphans();
  // P0-2：运行态归属收敛到末轮。新 user 已断开旧轮时，旧轮即使仍含 lastPersistedAssistantId 也不再 running；
  // 首个 assistant/tool 尚未落盘的间隙里，没有轮满足 containsActive，此时末轮兜底 running，live 段不再挂到旧轮上。
  // 已是“仅末轮 running”的常规态时零操作，避免无谓重建引用。
  if (opts.isGenerating === true) {
    let lastIdx = -1;
    for (let i = out.length - 1; i >= 0; i--) {
      if (out[i].kind === 'user-turn') {
        lastIdx = i;
        break;
      }
    }
    if (lastIdx >= 0) {
      const needFix = out.some((e, i) => e.kind === 'user-turn' && e.turn.isRunning !== (i === lastIdx));
      if (needFix) {
        for (let i = 0; i < out.length; i++) {
          const e = out[i];
          if (e.kind !== 'user-turn') continue;
          const t = e.turn;
          const shouldRun = i === lastIdx;
          t.isRunning = shouldRun;
          if (!shouldRun) {
            // 非末轮只收回运行态标志；phase/耗时/汇总保持 finalizeTurn 算好的 historical 值，
            // 绝不按 ended→now 重算，避免历史摘要被就地改写（review 反馈）。
            continue;
          }
          // 末轮被兜底点亮时，phase/耗时/汇总必须同步刷新，否则会停在「已完成」。
          const stopped = opts.stoppedTurnKeys ? opts.stoppedTurnKeys.has(t.key) : false;
          const phase: TurnPhase = turnPhaseOf({ hasOwnError: t.failedCount > 0, stopped, isActive: true, hasContent: t.contentChars > 0 });
          const duration = durationLabel(t.startedAtUtc, new Date(now).toISOString(), now);
          t.phase = phase;
          t.phaseLabel = duration ? `${phaseWordOf(phase)} · ${duration}` : phaseWordOf(phase);
          t.summary = summaryOf({ duration, toolCount: t.toolCount, failedCount: t.failedCount });
          t.fullSummary = fullSummaryOf({
            duration,
            toolCount: t.toolCount,
            failedCount: t.failedCount,
            contentChars: t.contentChars,
            reasoningChars: t.reasoningChars,
          });
        }
      }
    }
  }
  return out;
}

/** 汇总行状态图标（供 UserTurnSummary 直接用）。 */
export function phaseIcon(turn: UserTurn): string {
  return phaseIconOf(turn.phase);
}

/** 是否单步直出（单步无工具轮不折叠，05 R5）。 */
export function isSingleFlatTurn(turn: UserTurn): boolean {
  return turn.stepCount <= 1 && turn.toolCount === 0;
}

/** 末段正文（D1=B：在下直出；展开区不再重复）。 */
export function lastStepContent(turn: UserTurn): string {
  if (!turn.steps.length) return '';
  return String(turn.steps[turn.steps.length - 1]?.content || '');
}

/** 非末段步骤（展开区内容）。 */
export function earlierSteps(turn: UserTurn): UserTurnStep[] {
  if (turn.steps.length <= 1) return [];
  return turn.steps.slice(0, -1);
}

// ---- need02-P4（p4-proj）：增量投影 ----
// 指纹 = 消息条数 + 首尾 id + 聚合 opts（active/isGenerating/liveTotalMs/stopped 规模）。
// 命中即返回上次同一引用（模板零 diff，DOM 不抖）；未命中时若新输入是旧输入的前缀追加，
// 则复用旧 UserTurn 对象（仅重建末轮 + 新轮），否则全量重建。只读输入，不改传入对象（05 R1）。

export interface UserTurnsCache {
  fingerprint: string;
  messages: UnifiedChatMessage[];
  opts: BuildUserTurnsOpts;
  result: UserTurnEntry[];
}

function fingerprintOf(messages: UnifiedChatMessage[], opts: BuildUserTurnsOpts): string {
  const n = messages.length;
  const first = n > 0 ? String(messages[0]?.id || '') : '';
  const last = n > 0 ? String(messages[n - 1]?.id || '') : '';
  // 内容信号（Σ各条 content 长度 + 末条长度）：同 id 换内容（如重载改写）不得误命中旧引用。
  // 落盘消息 append-only，同 id 内容恒定；此处为启发式兜底 + 锁死测试语义。
  let contentLen = 0;
  for (let i = 0; i < n; i++) contentLen += String(messages[i]?.content || '').length;
  const lastLen = n > 0 ? String(messages[n - 1]?.content || '').length : 0;
  const stopped = opts.stoppedTurnKeys ? [...opts.stoppedTurnKeys].sort().join(',') : '';
  return [
    n,
    first,
    last,
    contentLen,
    lastLen,
    String(opts.activeAssistantId || ''),
    opts.isGenerating === true ? '1' : '0',
    String(opts.liveTotalMs ?? ''),
    stopped,
  ].join('|');
}

function isPrefixAppend(prev: UnifiedChatMessage[], next: UnifiedChatMessage[]): boolean {
  if (prev.length === 0 || prev.length >= next.length) return false;
  for (let i = 0; i < prev.length; i++) {
    if (String(prev[i]?.id || '') !== String(next[i]?.id || '')) return false;
  }
  return true;
}

/**
 * 增量聚合：cache 未命中且为纯追加时，只重建“旧末轮 + 新增部分”，其余 UserTurn 沿用旧引用。
 * 返回 { entries, reused }；reused=true 表示除末尾外全部复用（供测试断言与调试）。
 */
export function buildUserTurnsIncremental(
  messages: UnifiedChatMessage[] | null | undefined,
  opts: BuildUserTurnsOpts = {},
  cache?: UserTurnsCache | null
): { entries: UserTurnEntry[]; reused: boolean } {
  const list = Array.isArray(messages) ? messages : [];
  const fp = fingerprintOf(list, opts);
  if (cache && cache.fingerprint === fp) return { entries: cache.result, reused: true };
  let entries: UserTurnEntry[];
  let reused = false;
  if (cache && cache.result.length > 0 && isPrefixAppend(cache.messages, list)) {
    // 追加重建：旧结果除末条外整体复用；旧末轮可能被追加消息延续（如新 assistant 并入当前轮），故从旧末条起点重算。
    const kept = cache.result.slice(0, -1);
    // 找到旧末轮对应的消息起点：回退到旧结果末条所含的首条消息 id。
    const lastEntry = cache.result[cache.result.length - 1];
    let cutAt = cache.messages.length;
    if (lastEntry && lastEntry.kind === 'user-turn') {
      const t = lastEntry.turn;
      const headId = t.userMsg ? String(t.userMsg.id || '') : '';
      const stepHead = t.steps.length > 0 ? String(t.steps[0]?.assistantId || t.steps[0]?.toolMessages?.[0]?.id || '') : '';
      for (let i = 0; i < list.length; i++) {
        const id = String(list[i]?.id || '');
        if ((headId && id === headId) || (stepHead && id === stepHead)) {
          cutAt = i;
          break;
        }
      }
    } else if (lastEntry && lastEntry.kind === 'system') {
      const id = String(lastEntry.msg?.id || '');
      for (let i = 0; i < list.length; i++) {
        if (id && String(list[i]?.id || '') === id) {
          cutAt = i;
          break;
        }
      }
    }
    const rebuilt = buildUserTurns(list.slice(cutAt), opts);
    entries = [...kept, ...rebuilt];
    reused = true;
  } else {
    entries = buildUserTurns(list, opts);
  }
  if (cache) {
    cache.fingerprint = fp;
    cache.messages = list.slice();
    cache.opts = opts;
    cache.result = entries;
  }
  return { entries, reused };
}

/** 新建空缓存（调用方持有，供 timeline computed 闭包复用）。 */
export function createUserTurnsCache(): UserTurnsCache {
  return { fingerprint: '', messages: [], opts: {}, result: [] };
}

// ---- need02-P4（P4b#5）：轮级菜单纯函数 ----

/** 整轮消息 id（user + 各 step 的 assistant + tool），供整轮删除（逐条删，二次确认一次）。 */
export function turnMessageIds(turn: UserTurn): string[] {
  const out: string[] = [];
  if (turn.userMsg && turn.userMsg.id) out.push(String(turn.userMsg.id));
  for (const st of turn.steps || []) {
    if (st.assistantId) out.push(String(st.assistantId));
    for (const m of st.toolMessages || []) {
      if (m && m.id) out.push(String(m.id));
    }
  }
  return out;
}

/** 整轮复制文本（user 全文 + 各 step 全文 + 工具结果，阅读序）。 */
export function turnCopyText(turn: UserTurn): string {
  const parts: string[] = [];
  if (turn.userMsg && String(turn.userMsg.content || '').trim()) parts.push(String(turn.userMsg.content));
  for (const st of turn.steps || []) {
    if (String(st.content || '').trim()) parts.push(String(st.content));
    if (st.reasoning && String(st.reasoning).trim()) parts.push(String(st.reasoning));
    for (const m of st.toolMessages || []) {
      const c = String(m?.content || '').trim();
      if (c) parts.push(`[工具 ${String((m as unknown as { name?: unknown }).name || '')}] ${c}`);
    }
  }
  return parts.join('\n\n');
}

/** 整轮重放锚点（D3=A：重试另起一轮；user 锚优先，孤儿轮用末 assistant 锚）。 */
export function turnReplayAnchor(turn: UserTurn): { kind: 'user' | 'assistant'; id: string } | null {
  if (turn.userMsg && turn.userMsg.id && !String(turn.userMsg.id).startsWith('pending-')) {
    return { kind: 'user', id: String(turn.userMsg.id) };
  }
  for (let i = turn.steps.length - 1; i >= 0; i--) {
    const sid = String(turn.steps[i]?.assistantId || '');
    if (sid) return { kind: 'assistant', id: sid };
  }
  return null;
}
