/**
 * need-01 P1：无正文轮次合并（纯展示层聚合，不改落盘结构、不改事件契约）。
 *
 * 背景：后端每个 ReAct iteration 必落一条 assistant（content 允许为 null），
 * 于是 `buildTurnGroups` 的粒度是 iteration，UI 上「一次提问做了 8 步」会变成 8 个雷同折叠舱。
 * 本文件在其**下游**把「连续、无正文」的 turn 组折叠成一个 TurnRun（汇总舱）。
 *
 * 红线（docs/need-01/05）：
 *  · 只读 TurnGroup，只新建 TurnRun / entry 对象，绝不就地修改 g.toolMessages / g.content；
 *  · run.key 必须确定性稳定（= 'run-' + 首组 key），追加新组时不得变化；
 *  · Toc 仍消费未聚合的 entries（buildHistoryToc 只认 user/system/turn 三种 kind）。
 */
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from './turnGroup';
import { turnDurationLabel, turnPhaseOf, type TurnPhase } from './turnProcess';

/** 无正文轮次合并体（不落盘、不进 store 持久态）。 */
export interface TurnRun {
  /** 稳定锚点 key：`run-${首个 group.key}`（确定性，见 05 陷阱 3）。 */
  key: string;
  /** 按时间正序。 */
  groups: TurnGroup[];
  stepCount: number;
  toolCount: number;
  failedCount: number;
  hasReasoning: boolean;
  /** groups[0].createdAtUtc（可空串）。 */
  startedAtUtc: string;
  /** 末组 createdAtUtc（可空串）。 */
  endedAtUtc: string;
  /**
   * 末组 assistantId 落在「活动轨」范围内。
   * 由调用方通过 opts.isAssistantActive 注入（turnRun 不感知流式态，便于裸测）。
   */
  isRunning: boolean;
  /** 末组 assistantId（= 最后一个已落盘的 iteration；摘要行可直接传给 turnPhaseOf/turnDurationLabel）。 */
  lastAssistantId: string;
}

/** 失败工具判定口径（与 useTimeline.failedToolCount / useTimeline.hasFailedTool 同源）。 */
export const TOOL_ERROR_MARK = '[Tool Error';

/** 该轮内失败的 tool 条数。 */
export function failedCountOfGroup(g: TurnGroup): number {
  const list = g.toolMessages || [];
  let n = 0;
  for (const m of list) if (String((m as UnifiedChatMessage).content || '').includes(TOOL_ERROR_MARK)) n += 1;
  return n;
}

/**
 * 无正文轮判定（唯一事实源）：
 *  · 孤立 tool 组（assistantId === ''）→ 无正文；
 *  · 其余以 content 是否为空为准（reasoning 不算正文——它意味着「只思考没输出」）。
 */
export function isGhostTurn(g: TurnGroup | undefined | null): boolean {
  if (!g) return false;
  if (g.assistantId === '') return true;
  return !String(g.content ?? '').trim();
}

/** 是否还有可展开的明细（reasoning 或任一工具）。 */
export function hasRunDetail(run: TurnRun): boolean {
  if (run.hasReasoning) return true;
  return run.toolCount > 0;
}

export interface BuildTurnRunsOpts {
  /** 末组是否仍处于「活动轨」（流式进行中）；缺省视为非运行态。 */
  isAssistantActive?: (assistantId: string) => boolean;
}

/** 由若干组构造 run（派生量一次性算好，避免模板里反复 reduce）。 */
export function makeTurnRun(groups: TurnGroup[], opts: BuildTurnRunsOpts = {}): TurnRun {
  const first = groups[0];
  const last = groups[groups.length - 1];
  let toolCount = 0;
  let failedCount = 0;
  let hasReasoning = false;
  for (const g of groups) {
    toolCount += (g.toolMessages || []).length;
    failedCount += failedCountOfGroup(g);
    if (!hasReasoning && g.reasoning) hasReasoning = true;
  }
  const lastAssistantId = String(last?.assistantId || '');
  return {
    key: `run-${String(first?.key || '')}`,
    groups,
    stepCount: groups.length,
    toolCount,
    failedCount,
    hasReasoning,
    startedAtUtc: String(first?.createdAtUtc || ''),
    endedAtUtc: String(last?.createdAtUtc || ''),
    isRunning: lastAssistantId !== '' && (opts.isAssistantActive ? opts.isAssistantActive(lastAssistantId) : false),
    lastAssistantId,
  };
}

/**
 * 把「连续、无正文」的 turn 组折叠成 run；遇有正文 turn / user / system 即断开。
 *
 * 边界规则（docs/need-01/03 §3.2.2）：
 *  · user / system        → 断开；
 *  · turn 且 !isGhostTurn → 断开，自身独立渲染（有正文必须直出）；
 *  · turn 且 isGhostTurn  → 并入当前 run（无则新建）；
 *  · run 内只有 1 个 group → 不合并，退化为普通 turn（决策 D1 = A）。
 *
 * 只新建 entry 对象，绝不修改传入的 TurnGroup / TimelineEntry（05 陷阱 1）。
 */
export function buildTurnRuns(entries: TimelineEntry[], opts: BuildTurnRunsOpts = {}): TimelineEntry[] {
  const out: TimelineEntry[] = [];
  let buf: TurnGroup[] = [];
  const flush = (): void => {
    if (!buf.length) return;
    const groups = buf;
    buf = [];
    if (groups.length < 2) {
      const g = groups[0];
      out.push({ key: g.key, kind: 'turn', group: g });
      return;
    }
    const run = makeTurnRun(groups, opts);
    out.push({ key: run.key, kind: 'turn-run', run });
  };
  for (const e of entries) {
    if (e.kind === 'turn' && e.group && isGhostTurn(e.group)) {
      buf.push(e.group);
      continue;
    }
    flush();
    out.push(e);
  }
  flush();
  return out;
}

/** 汇总行文案（纯函数，便于快照测试）：`执行过程 · 8 步 · 23 个工具 · 失败 2`。 */
export function runSummaryText(run: TurnRun): string {
  const parts = ['执行过程', `${run.stepCount} 步`];
  if (run.toolCount > 0) parts.push(`${run.toolCount} 个工具`);
  if (run.failedCount > 0) parts.push(`失败 ${run.failedCount}`);
  return parts.join(' · ');
}

/** 汇总行状态词（run 级只出一次，不复用 TurnGroupItem 的 phaseLabel —— 05 陷阱 10）。 */
export function runPhaseOf(run: TurnRun, input: { failed: boolean; stopped: boolean }): TurnPhase {
  return turnPhaseOf({ hasOwnError: input.failed, stopped: input.stopped, isActive: run.isRunning, hasContent: false });
}

/** 汇总行耗时（沿用 turnProcess.turnDurationLabel 的口径，run 级只算一次）。 */
export function runDurationLabel(run: TurnRun, now = Date.now()): string {
  const started = Date.parse(String(run.startedAtUtc || ''));
  if (!Number.isFinite(started) || started <= 0) return '';
  return turnDurationLabel(started, now);
}

/** 状态词 + 耗时的完整汇总行（`失败 2 · For 12s`）。 */
export function runPhaseLabel(run: TurnRun, input: { failed: boolean; stopped: boolean }, now = Date.now()): string {
  const dur = runDurationLabel(run, now);
  const suffix = dur ? ` · ${dur}` : '';
  const heads: Record<TurnPhase, string> = {
    failed: `失败${suffix}`,
    stopped: `已停止${suffix}`,
    running: `运行中…${suffix}`,
    completed: `已完成${suffix}`,
  };
  return heads[runPhaseOf(run, input)];
}
