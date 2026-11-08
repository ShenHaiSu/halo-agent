// P02 单容器三段适配器单测（6 例：单步/多步原序/tool 原位/live 归槽/key 不变/旧 kind 降级）。
import { describe, expect, it } from 'vitest';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { buildUserTurns } from './userTurn';
import {
  buildTurnRenderView,
  flattenTurnRunsForRender,
  groupOf,
  isLiveOwner,
  pendingTailSlot,
  stepViewOf,
  turnStepAnchorKey,
} from './turnRender';

const u = (id: string, content: string): UnifiedChatMessage =>
  ({ id, role: 'user', content, createdAtUtc: '2026-10-26T11:59:00Z' }) as unknown as UnifiedChatMessage;
const a = (id: string, content: string): UnifiedChatMessage =>
  ({ id, role: 'assistant', content, createdAtUtc: '2026-10-26T11:59:10Z' }) as unknown as UnifiedChatMessage;
const t = (id: string, callId: string, content: string): UnifiedChatMessage =>
  ({ id, role: 'tool', toolCallId: callId, content, createdAtUtc: '2026-10-26T11:59:20Z' }) as unknown as UnifiedChatMessage;

function firstTurn(msgs: UnifiedChatMessage[]) {
  const entries = buildUserTurns(msgs, { now: Date.parse('2026-10-26T12:00:00Z') });
  const e = entries[0];
  if (!e || e.kind !== 'user-turn') throw new Error('not a user turn');
  return e.turn;
}

describe('turnRender 适配器', () => {
  it('1. 单步直转：content/reasoning/tool 原样进 StepView', () => {
    const turn = firstTurn([u('u1', '你好'), a('a1', '世界')]);
    const view = buildTurnRenderView(turn);
    expect(view.key).toBe(turn.key);
    expect(view.steps).toHaveLength(1);
    expect(view.steps[0].content).toBe('世界');
    expect(stepViewOf(turn.steps[0]).assistantId).toBe('a1');
  });

  it('2. 多步原序：iteration 升序且与 steps 一一对应', () => {
    const turn = firstTurn([u('u1', 'x'), a('a1', '第一段'), a('a2', ''), a('a3', '末段')]);
    const view = buildTurnRenderView(turn);
    expect(view.steps.map((s) => s.iteration)).toEqual([1, 2, 3]);
    expect(view.steps.map((s) => s.assistantId)).toEqual(['a1', 'a2', 'a3']);
  });

  it('3. tool 原位：toolMessages 跟随所属 step，groupOf 薄壳 key 稳定', () => {
    const turn = firstTurn([u('u1', 'x'), a('a1', '第一段'), t('t1', 'c1', 'out'), a('a2', '末段')]);
    const view = buildTurnRenderView(turn);
    expect(view.steps[0].toolMessages.map((m) => String(m.id))).toEqual(['t1']);
    expect(view.steps[1].toolMessages).toHaveLength(0);
    expect(groupOf(view.steps[0]).key).toBe('a1');
    expect(groupOf(view.steps[0]).toolMessages).toHaveLength(1);
  });

  it('4. live 归槽：已落盘 tool 不进尾槽，未归属文本/新 tool 进尾槽', () => {
    const turn = firstTurn([u('u1', 'x'), a('a1', '第一段'), t('t1', 'c1', 'out')]);
    const tail = pendingTailSlot(
      [
        { kind: 'tool', key: 'seg-c1', card: { callId: 'c1' } },
        { kind: 'text', key: 'seg-text-1', text: '草稿' },
        { kind: 'tool', key: 'seg-c9', card: { callId: 'c9' } },
      ],
      turn
    );
    expect(tail.map((s) => s.key)).toEqual(['seg-text-1', 'seg-c9']);
  });

  it('5. key 不变：视图 key 与 turn.key 一致，锚点格式恒定', () => {
    const turn = firstTurn([u('u1', 'x'), a('a1', 'y')]);
    expect(buildTurnRenderView(turn).key).toBe(turn.key);
    expect(turnStepAnchorKey(turn.key, 2)).toBe(`${turn.key}:step-2`);
  });

  it('6. 旧 kind 降级：turn-run 展平为 turn，system/user 原样透传', () => {
    const g = (id: string) =>
      ({
        key: id,
        assistantId: id,
        reasoning: null,
        content: null,
        toolCalls: [],
        toolMessages: [],
        telemetry: null,
        createdAtUtc: '',
      }) as never;
    const runEntry = {
      key: 'run-g1',
      kind: 'turn-run',
      run: { key: 'run-g1', groups: [g('g1'), g('g2')], isRunning: false },
    } as never;
    const sysEntry = { key: 's-1', kind: 'system', msg: { id: 's1' } } as never;
    const flat = flattenTurnRunsForRender([runEntry, sysEntry]);
    expect(flat.map((e) => `${e.kind}:${e.key}`)).toEqual(['turn:g1', 'turn:g2', 'system:s-1']);
  });

  it('7. Live 归属：仅末用户轮拥有流式段', () => {
    const items = [
      { kind: 'user-turn', key: 'uturn-u1', turn: { key: 'uturn-u1' } },
      { kind: 'system', key: 's-1' },
      { kind: 'user-turn', key: 'uturn-u2', turn: { key: 'uturn-u2' } },
    ];
    expect(isLiveOwner(items[0], items)).toBe(false);
    expect(isLiveOwner(items[2], items)).toBe(true);
    expect(isLiveOwner(items[1], items)).toBe(false);
  });
});
