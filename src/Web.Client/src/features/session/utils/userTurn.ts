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
    .map((tc) => ({ id: String((tc as { id?: string }).id || ''), name: String(tc.function?.name || (tc as { name?: string }).name || 'tool') }))
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
export function fullSummaryOf(input: { duration: string; toolCount: number; failedCount: number; contentChars: number; reasoningChars: number }): string {
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
      ? { inputTokens: assistant.inputTokens ?? null, cachedTokens: assistant.cachedTokens ?? null, outputTokens: assistant.outputTokens ?? null }
      : null;
  return {
    iteration,
    assistantId: String(assistant?.id || ''),
    content,
    reasoning: reasoning ? String(reasoning) : null,
    toolMessages: tools.slice(),
    toolCalls,
    telemetry,
    createdAtUtc: String((assistant as unknown as { createdAtUtc?: string } | null)?.createdAtUtc || tools[tools.length - 1]?.createdAtUtc || ''),
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
  const isRunning = generating && (containsActive || (lastStepActive && built.length > 0) || (activeId === '' && built.length === 0 && !!userMsg));
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
   const emitTurn = (userMsg: UnifiedChatMessage | null, steps: Array<{ assistant: UnifiedChatMessage | null; tools: UnifiedChatMessage[] }>, requestId: string | null): void => {
     if (!userMsg && steps.length === 0) return;
     const t = finalizeTurn({ userMsg, orphanHeadId: userMsg ? String(userMsg.id || '') : orphanHeadId || `orphan-${out.length}`, steps, requestId, opts, now });
     out.push({ key: t.key, kind: 'user-turn', turn: t });
   };
   const flushOrphans = (): void => {
     if (!orphanSteps.length) return;
     const t = finalizeTurn({ userMsg: null, orphanHeadId: orphanHeadId || `orphan-${out.length}`, steps: orphanSteps, requestId: null, opts, now });
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
     out.push({ key: `s-${String((msg as UnifiedChatMessage).id || `unknown-${out.length}`)}`, kind: 'system', msg: msg as UnifiedChatMessage });
   }
   pushStep(curUser ? curSteps : orphanSteps);
   if (curUser || curSteps.length) emitTurn(curUser, curSteps, curRequestId);
   else flushOrphans();
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
