/** M4 收敛项：settle veil 语义 + 增量归并等价性（§2 验收 / §3.2 正确性）。 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createSettleController, FADE_MS, MAX_MS, STABLE_FRAMES } from './transcriptSettle';
import { buildTurnGroups, buildTurnGroupsIncremental } from './turnGroup';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

const msg = (id: string, role: string, extra: Record<string, unknown> = {}): UnifiedChatMessage =>
  ({ id, role, content: `c-${id}`, createdAtUtc: '', ...extra }) as unknown as UnifiedChatMessage;

describe('settle veil 控制器（M4-§2）', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('短会话直接 off，不遮罩', () => {
    const ctrl = createSettleController({ shortSession: true });
    expect(ctrl.veilCovering).toBe(false);
    expect(ctrl.phase).toBe('off');
  });

  it('连续 3 帧尺寸稳定即收口，并在淡出后摘掉遮罩', () => {
    const ctrl = createSettleController();
    expect(ctrl.veilCovering).toBe(true);
    const sample = { scrollHeight: 1000, clientHeight: 400 };
    for (let i = 0; i < STABLE_FRAMES; i++) ctrl.sample(sample);
    expect(ctrl.phase).toBe('leaving');
    // 淡出期间仍盖着（避免提前露出未稳定的内容）
    expect(ctrl.veilCovering).toBe(true);
    vi.advanceTimersByTime(FADE_MS);
    expect(ctrl.veilCovering).toBe(false);
    expect(ctrl.phase).toBe('off');
  });

  it('高度持续变化时由 600ms 上限兜底结束（不会永久遮挡）', () => {
    const ctrl = createSettleController();
    ctrl.sample({ scrollHeight: 100, clientHeight: 400 });
    ctrl.sample({ scrollHeight: 200, clientHeight: 400 });
    expect(ctrl.veilCovering).toBe(true);
    vi.advanceTimersByTime(MAX_MS);
    expect(ctrl.phase).toBe('leaving');
    vi.advanceTimersByTime(FADE_MS);
    expect(ctrl.veilCovering).toBe(false);
  });

  it('onPhase 在 leaving / off 各回调一次（供组件同步 veilCovering）', () => {
    const ctrl = createSettleController();
    const phases: string[] = [];
    ctrl.onPhase = (p) => phases.push(p);
    const sample = { scrollHeight: 10, clientHeight: 10 };
    for (let i = 0; i < STABLE_FRAMES; i++) ctrl.sample(sample);
    vi.advanceTimersByTime(FADE_MS);
    expect(phases).toEqual(['leaving', 'off']);
  });
});

describe('增量归并（M4-§3.2）', () => {
  const base = [msg('u1', 'user'), msg('a1', 'assistant', { toolCalls: [{ id: 't1', function: { name: 'read_file' } }] })];

  it('追加工具消息：增量结果与全量一致', () => {
    const first = buildTurnGroupsIncremental([], 0, base);
    const withTool = [...base, msg('t1', 'tool', { toolCallId: 't1' })];
    const inc = buildTurnGroupsIncremental(first.groups, first.msgCount, withTool);
    expect(inc.groups).toEqual(buildTurnGroups(withTool));
    expect(inc.msgCount).toBe(withTool.length);
  });

  it('连续追加多个工具消息：每次都与全量等价', () => {
    let groups = buildTurnGroupsIncremental([], 0, base).groups;
    let count = base.length;
    let msgs = [...base];
    for (const id of ['t1', 't2', 't3']) {
      msgs = [...msgs, msg(id, 'tool', { toolCallId: id })];
      const r = buildTurnGroupsIncremental(groups, count, msgs);
      expect(r.groups).toEqual(buildTurnGroups(msgs));
      groups = r.groups;
      count = r.msgCount;
    }
  });

  it('已知末轮追加工具时复用历史组引用（增量优化生效的证据）', () => {
    const first = buildTurnGroupsIncremental([], 0, [...base, msg('a2', 'assistant')]);
    const next = [...base, msg('a2', 'assistant'), msg('t9', 'tool', { toolCallId: 't9' })];
    const r = buildTurnGroupsIncremental(first.groups, first.msgCount, next);
    expect(r.groups).toEqual(buildTurnGroups(next));
    expect(r.groups[0]).toBe(first.groups[0]);
  });

  it('新增一轮 assistant：结果与全量一致（末 assistant 不在旧投影 → 整体回退）', () => {
    const first = buildTurnGroupsIncremental([], 0, base);
    const next = [...base, msg('u2', 'user'), msg('a2', 'assistant')];
    const r = buildTurnGroupsIncremental(first.groups, first.msgCount, next);
    expect(r.groups).toEqual(buildTurnGroups(next));
  });

  it('消息减少（历史删改）时回退全量，结果正确', () => {
    const first = buildTurnGroupsIncremental([], 0, [...base, msg('u2', 'user')]);
    const shrunk = [msg('u1', 'user')];
    const r = buildTurnGroupsIncremental(first.groups, first.msgCount, shrunk);
    expect(r.groups).toEqual(buildTurnGroups(shrunk));
  });

  it('末 assistant 落在旧前缀内且后缀非纯 tool 时回退全量', () => {
    const first = buildTurnGroupsIncremental([], 0, [msg('u1', 'user'), msg('a1', 'assistant')]);
    const changed = [msg('u1', 'user'), msg('a1', 'assistant'), msg('u2', 'user')];
    const r = buildTurnGroupsIncremental(first.groups, first.msgCount, changed);
    expect(r.groups).toEqual(buildTurnGroups(changed));
  });
});
