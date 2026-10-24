/**
 * need-01 P1：无正文轮次合并纯函数单测（docs/need-01/04 §Phase 1 的 7 条用例）。
 *
 * 对齐 f05-timeline.spec.ts 的 msg 构造器写法；run 聚合发生在 buildTimelineEntries 的下游，
 * 因此这里用「真实 buildTurnGroups + buildTimelineEntries」造 entries，避免手搓假组掩盖归并语义。
 */
import { describe, expect, it } from 'vitest';
import { buildTimelineEntries, buildTurnGroups, type TimelineEntry, type TurnGroup } from '@features/session/utils/turnGroup';
import {
  buildTurnRuns,
  failedCountOfGroup,
  isGhostTurn,
  makeTurnRun,
  runDurationLabel,
  runPhaseLabel,
  runSummaryText,
  type TurnRun,
} from '@features/session/utils/turnRun';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

type Extra = Record<string, unknown>;

function msg(id: string, role: string, content: string | null, extra: Extra = {}): UnifiedChatMessage {
  return { id, role, content, createdAtUtc: '2026-10-24T00:00:00Z', ...extra } as unknown as UnifiedChatMessage;
}

/** 一步无正文迭代：assistant(content=null) + 若干 tool 回包。 */
function ghostStep(idx: number, toolCount = 1, opts: { reasoning?: string; failTools?: number } = {}): UnifiedChatMessage[] {
  const callIds = Array.from({ length: toolCount }, (_, i) => `c${idx}-${i}`);
  const asst = msg(`a${idx}`, 'assistant', null, {
    ...(opts.reasoning ? { reasoningContent: opts.reasoning } : {}),
    toolCalls: callIds.map((c) => ({ id: c, type: 'function', function: { name: 'tool', arguments: '{}' } })),
  });
  const tools = callIds.map((c, i) =>
    msg(`t${idx}-${i}`, 'tool', i < (opts.failTools ?? 0) ? '[Tool Error js] bang' : 'out', { toolCallId: c })
  );
  return [asst, ...tools];
}

function ghostStepGroups(idx: number, toolCount = 1, opts: { reasoning?: string; failTools?: number } = {}): TurnGroup[] {
  return buildTurnGroups(ghostStep(idx, toolCount, opts));
}

/** 8 个无正文迭代 → 真实 entries（含一条用户提问在最前）。 */
function eightGhostEntries(): ReturnType<typeof buildTimelineEntries> {
  const msgs: UnifiedChatMessage[] = [msg('u1', 'user', '帮我查一下仓库状态')];
  for (let i = 1; i <= 8; i++) msgs.push(...ghostStep(i));
  return buildTimelineEntries(msgs, buildTurnGroups(msgs));
}

describe('isGhostTurn', () => {
  it('content 为空 / 纯空白 / 孤立 tool 组都算无正文；有正文不算', () => {
    expect(isGhostTurn(ghostStepGroups(1)[0])).toBe(true);
    expect(isGhostTurn(buildTurnGroups([msg('a9', 'assistant', '   ')])[0])).toBe(true);
    expect(isGhostTurn(buildTurnGroups([msg('t9', 'tool', 'out')])[0])).toBe(true);
    expect(isGhostTurn(buildTurnGroups([msg('a1', 'assistant', '有正文')])[0])).toBe(false);
    expect(isGhostTurn(undefined)).toBe(false);
  });

  it('只有 reasoning 没有正文 → 仍是 ghost（只思考没输出）', () => {
    const g = ghostStepGroups(2, 0, { reasoning: '让我先看看目录' })[0];
    expect(isGhostTurn(g)).toBe(true);
  });
});

describe('buildTurnRuns', () => {
  it('用例 1：3 个连续无正文 turn 合并为 1 个 run', () => {
    const entries = buildTurnRuns(
      [...ghostStepGroups(1), ...ghostStepGroups(2), ...ghostStepGroups(3)].map((g) => ({
        key: g.key,
        kind: 'turn' as const,
        group: g,
      }))
    );
    expect(entries).toHaveLength(1);
    expect(entries[0]?.kind).toBe('turn-run');
    expect(entries[0]?.run?.groups).toHaveLength(3);
    expect(entries[0]?.run?.stepCount).toBe(3);
  });

  it('用例 1b（G1 验收口径）：8 个无正文 iteration → 1 个汇总舱，而非 8 个', () => {
    const raw = eightGhostEntries();
    expect(raw.filter((e) => e.kind === 'turn')).toHaveLength(8);
    const merged = buildTurnRuns(raw);
    // user + 1 个 run
    expect(merged.map((e) => e.kind)).toEqual(['user', 'turn-run']);
    expect(merged[1]?.run?.stepCount).toBe(8);
    expect(merged[1]?.run?.toolCount).toBe(8);
  });

  it('用例 2：无正文 turn 之间夹一条有正文 turn → 断开成 run + turn + run', () => {
    const msgs: UnifiedChatMessage[] = [
      ...ghostStep(1),
      ...ghostStep(2),
      msg('a3', 'assistant', '我把结果整理如下'),
      ...ghostStep(4),
      ...ghostStep(5),
    ];
    const merged = buildTurnRuns(buildTimelineEntries(msgs, buildTurnGroups(msgs)));
    expect(merged.map((e) => (e.kind === 'turn-run' ? 'run' : e.kind))).toEqual(['run', 'turn', 'run']);
    expect(merged[1]?.key).toBe('a3'); // 有正文轮原样直出（引用不变）
  });

  it('用例 3：run 内只有 1 组 → 不合并，原样返回 kind: "turn"（D1 = A）', () => {
    const g = ghostStepGroups(1, 2)[0]!;
    const entry = { key: g.key, kind: 'turn' as const, group: g };
    const merged = buildTurnRuns([entry]);
    expect(merged).toHaveLength(1);
    expect(merged[0]?.kind).toBe('turn');
    expect(merged[0]?.run).toBeUndefined();
    expect(merged[0]?.group).toBe(g);
  });

  it('用例 4：user / system 分隔 → 断开 run', () => {
    const msgs: UnifiedChatMessage[] = [
      ...ghostStep(1),
      ...ghostStep(2),
      msg('u2', 'user', '第二个问题'),
      ...ghostStep(3),
      ...ghostStep(4),
      msg('s5', 'system', '系统提示'),
      ...ghostStep(6),
      ...ghostStep(7),
    ];
    const merged = buildTurnRuns(buildTimelineEntries(msgs, buildTurnGroups(msgs)));
    expect(merged.map((e) => (e.kind === 'turn-run' ? 'run' : e.kind))).toEqual(['run', 'user', 'run', 'system', 'run']);
  });

  it('用例 5：孤立 tool 组（assistantId === ""）并入相邻 run', () => {
    const orphan = buildTurnGroups([msg('tZ', 'tool', 'out')])[0]!;
    expect(orphan.assistantId).toBe('');
    const entries = [
      ...ghostStepGroups(1).map((g) => ({ key: g.key, kind: 'turn' as const, group: g })),
      { key: orphan.key, kind: 'turn' as const, group: orphan },
      ...ghostStepGroups(2).map((g) => ({ key: g.key, kind: 'turn' as const, group: g })),
    ];
    const merged = buildTurnRuns(entries);
    expect(merged).toHaveLength(1);
    expect(merged[0]?.run?.groups).toHaveLength(3);
    // 老数据兼容：全部都是孤立 tool 组时，同样合并（长度 ≥ 2）
    const onlyOrphans = buildTurnGroups([msg('t1', 'tool', 'a'), msg('t2', 'tool', 'b')]).map((g) => ({
      key: g.key,
      kind: 'turn' as const,
      group: g,
    }));
    expect(onlyOrphans).toHaveLength(2);
    expect(buildTurnRuns(onlyOrphans)[0]?.kind).toBe('turn-run');
  });

  it('用例 6：求和正确性（toolCount / failedCount，失败判定含 [Tool Error 前缀）', () => {
    const a = ghostStepGroups(1, 3, { failTools: 1 })[0]!;
    const b = ghostStepGroups(2, 2, { failTools: 1 })[0]!;
    const c = ghostStepGroups(3, 1, { failTools: 0 })[0]!;
    const run = makeTurnRun([a, b, c]);
    expect(run.toolCount).toBe(6);
    expect(run.failedCount).toBe(2);
    expect(failedCountOfGroup(a)).toBe(1);
    expect(failedCountOfGroup(c)).toBe(0);
    expect(run.hasReasoning).toBe(false);
    expect(makeTurnRun([ghostStepGroups(4, 0, { reasoning: '想一想' })[0]!]).hasReasoning).toBe(true);
  });

  it('用例 7：key 稳定性 —— 同输入两次一致；追加第 4 组后前 3 组的 run.key 不变', () => {
    const g1 = ghostStepGroups(1);
    const upto3 = buildTurnRuns(
      g1.concat(ghostStepGroups(2), ghostStepGroups(3)).map((g) => ({ key: g.key, kind: 'turn' as const, group: g }))
    );
    const again = buildTurnRuns(
      g1.concat(ghostStepGroups(2), ghostStepGroups(3)).map((g) => ({ key: g.key, kind: 'turn' as const, group: g }))
    );
    expect(upto3[0]?.key).toBe(again[0]?.key);
    expect(upto3[0]?.key).toBe(`run-${g1[0]!.key}`);

    const upto4 = buildTurnRuns(
      g1.concat(ghostStepGroups(2), ghostStepGroups(3), ghostStepGroups(4)).map((g) => ({ key: g.key, kind: 'turn' as const, group: g }))
    );
    expect(upto4[0]?.key).toBe(upto3[0]?.key);
    expect(upto4[0]?.run?.stepCount).toBe(4);
  });

  it('红线（05 陷阱 1）：只新建对象，不就地修改 TurnGroup', () => {
    const groups = ghostStepGroups(1, 2).concat(ghostStepGroups(2, 1));
    const snapshot = groups.map((g) => ({ tools: g.toolMessages.length, content: g.content }));
    const merged = buildTurnRuns(groups.map((g) => ({ key: g.key, kind: 'turn' as const, group: g })));
    expect(merged[0]?.run?.groups[0]).toBe(groups[0]); // 引用复用（下游 memo 依赖）
    expect(groups.map((g) => ({ tools: g.toolMessages.length, content: g.content }))).toEqual(snapshot);
    // 原 entry 对象也不被就地改写
    const entries: TimelineEntry[] = groups.map((g) => ({ key: g.key, kind: 'turn' as const, group: g }));
    buildTurnRuns(entries);
    expect(entries.every((e) => e.run === undefined && e.kind === 'turn')).toBe(true);
  });

  it('isAssistantActive 注入：末组落在活动轨时 isRunning 为真（单步退化也保留运行态）', () => {
    const [g] = ghostStepGroups(1);
    const run = makeTurnRun([g!], { isAssistantActive: (id) => id === 'a1' });
    expect(run.isRunning).toBe(true);
    expect(run.lastAssistantId).toBe('a1');
    expect(makeTurnRun([g!]).isRunning).toBe(false);
  });
});

describe('汇总行文案', () => {
  const g = ghostStepGroups(1, 3, { failTools: 2 })[0]!;
  const run: TurnRun = {
    ...makeTurnRun([g]),
    groups: Array.from({ length: 8 }, () => g),
    stepCount: 8,
    toolCount: 23,
    failedCount: 2,
  };

  it('runSummaryText：步骤 / 工具 / 失败，0 值片段不出现', () => {
    expect(runSummaryText(run)).toBe('执行过程 · 8 步 · 23 个工具 · 失败 2');
    expect(runSummaryText({ ...run, toolCount: 0, failedCount: 0 })).toBe('执行过程 · 8 步');
  });

  it('runPhaseLabel：状态词只出一次，耗时口径沿用 turnDurationLabel', () => {
    const started = Date.parse('2026-10-24T00:00:00Z');
    const at = started + 12_000;
    const stopped = { ...run, isRunning: false };
    expect(runPhaseLabel(stopped, { failed: false, stopped: false }, at)).toBe('已完成 · For 12s');
    expect(runPhaseLabel(stopped, { failed: true, stopped: false }, at)).toBe('失败 · For 12s');
    expect(runPhaseLabel(stopped, { failed: false, stopped: true }, at)).toBe('已停止 · For 12s');
    expect(runPhaseLabel({ ...run, isRunning: true }, { failed: false, stopped: false }, at)).toBe('运行中… · For 12s');
    // 耗时无法计算时省略
    expect(runDurationLabel({ ...run, startedAtUtc: '' })).toBe('');
    expect(runPhaseLabel({ ...run, isRunning: false, startedAtUtc: '' }, { failed: false, stopped: false })).toBe('已完成');
  });
});
