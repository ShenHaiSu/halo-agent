// 流态装配保留本轮全量历史：已毕业分段继续渲染，终态快照补缺被剪卡片。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { haloEventBus } from '@shared/bridge/hostBridge';
import {
  ensureTurn,
  markIterationTextPersisted,
  markToolSegPersisted,
  pushToolSeg,
  setActiveSessionId,
  turns,
} from '@features/agent/stores/model';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useTimelineViewModel } from './useTimelineViewModel';

const SID = 'session-live-history';

function addCard(
  t: ReturnType<typeof ensureTurn>,
  callId: string,
  patch: Partial<{ functionName: string; argsJson: string; status: 'running' | 'done' | 'error'; resultText: string }> = {}
): void {
  t.toolCards.push({
    callId,
    functionName: patch.functionName ?? 'search',
    argsJson: patch.argsJson ?? '{"q":"1"}',
    status: patch.status ?? 'done',
    resultText: patch.resultText ?? 'ok',
    exitCode: null,
    durationMs: null,
    iteration: t.curIteration,
    receivedBytes: 8,
  });
}

function emitIterationStarted(iteration: number): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId: SID, kind: 'IterationStarted', payload: { iteration } });
}

function graduateToolCall(callId: string): void {
  const t = ensureTurn(SID);
  const card = t.toolCards.find((c) => c.callId === callId);
  if (card) card.persisted = true;
  markToolSegPersisted(t, callId);
}

type ToolSeg = {
  kind: string;
  key: string;
  card: { callId: string; functionName: string; argsJson: string; status: string; resultText: string } | null;
};

beforeEach(() => {
  setActivePinia(createPinia());
  setActiveSessionId(SID);
  turns.value = {};
  useAgentStream(); // 绑定迭代切换监听（幂等）
});

describe('流态装配保留本轮全量历史', () => {
  it('毕业工具段保留并以终态快照渲染，新轮工具段正常跟随', () => {
    const t = ensureTurn(SID);
    t.requestId = 'run-old';
    t.curIteration = 1;
    pushToolSeg(t, 'call-old');
    addCard(t, 'call-old', { status: 'done', resultText: 'old-result' });
    graduateToolCall('call-old');

    emitIterationStarted(2);

    // 剪卡发生但快照留存：活卡表已无旧卡，快照持有终态。
    expect(t.toolCards.some((c) => c.callId === 'call-old')).toBe(false);
    expect(t.graduatedCards.some((g) => g.callId === 'call-old')).toBe(true);

    t.requestId = 'run-new';
    pushToolSeg(t, 'call-new');
    addCard(t, 'call-new', { status: 'running', resultText: '' });

    const vm = useTimelineViewModel();
    const segs = vm.liveInclusiveSegs.value as ToolSeg[];
    const toolSegs = segs.filter((s) => s.kind === 'tool');
    expect(toolSegs.map((s) => String(s.card?.callId || ''))).toEqual(['call-old', 'call-new']);

    const oldCard = toolSegs[0]?.card;
    expect(oldCard?.functionName).toBe('search');
    expect(oldCard?.argsJson).toBe('{"q":"1"}');
    expect(oldCard?.resultText).toBe('old-result');
    expect(oldCard?.status).toBe('done');
    expect(toolSegs[1]?.card?.status).toBe('running');
  });

  it('毕业正文与思维链段保留且顺序在新段之前', () => {
    const t = ensureTurn(SID);
    t.curIteration = 1;
    t.segs.push({ key: 'text-1', kind: 'text', iteration: 1, text: 'history-text' });
    t.segs.push({ key: 'reason-1', kind: 'reasoning', iteration: 1, text: 'history-reason' });
    markIterationTextPersisted(t, 1);

    emitIterationStarted(2);

    t.segs.push({ key: 'text-2', kind: 'text', iteration: 2, text: 'live-text' });

    const vm = useTimelineViewModel();
    const segs = vm.liveInclusiveSegs.value as Array<{ kind: string; key: string; text: string }>;
    expect(segs.map((s) => s.key)).toEqual(['text-1', 'reason-1', 'text-2']);
    expect(segs.map((s) => s.text)).toEqual(['history-text', 'history-reason', 'live-text']);
  });

  it('当轮活卡优先于快照，参数随流增长不受旧快照污染', () => {
    const t = ensureTurn(SID);
    t.curIteration = 1;
    t.graduatedCards.push({
      callId: 'call-x',
      functionName: 'search',
      argsJson: '{"q":"stale"}',
      status: 'done',
      resultText: 'stale-result',
      exitCode: null,
      durationMs: null,
      iteration: 1,
      receivedBytes: 8,
    });
    t.curIteration = 2;
    pushToolSeg(t, 'call-x');
    addCard(t, 'call-x', { status: 'running', argsJson: '{"q":"fresh-partial"}', resultText: '' });

    const vm = useTimelineViewModel();
    const segs = vm.liveInclusiveSegs.value as ToolSeg[];
    const card = segs.find((s) => s.kind === 'tool')?.card;
    expect(card?.status).toBe('running');
    expect(card?.resultText).toBe('');
    expect(card?.argsJson).toBe('{"q":"fresh-partial"}');
  });

  it('调用声明到达但参数尚未到达时仍回退占位', () => {
    const t = ensureTurn(SID);
    t.curIteration = 2;
    pushToolSeg(t, 'call-lonely');

    const vm = useTimelineViewModel();
    const segs = vm.liveInclusiveSegs.value as ToolSeg[];
    const card = segs.find((s) => s.kind === 'tool')?.card;
    expect(card?.callId).toBe('call-lonely');
    expect(card?.functionName).toBe('tool');
    expect(card?.status).toBe('running');
    expect(card?.argsJson).toBe('');
  });

  it('连续三轮切换后流态为全量追加序', () => {
    const t = ensureTurn(SID);
    t.requestId = 'run-1';
    t.curIteration = 1;
    pushToolSeg(t, 'call-1');
    addCard(t, 'call-1', { status: 'done', resultText: 'r1' });
    t.segs.push({ key: 'text-1', kind: 'text', iteration: 1, text: 't1' });
    graduateToolCall('call-1');
    markIterationTextPersisted(t, 1);

    emitIterationStarted(2);
    t.requestId = 'run-2';
    pushToolSeg(t, 'call-2');
    addCard(t, 'call-2', { status: 'done', resultText: 'r2' });
    t.segs.push({ key: 'text-2', kind: 'text', iteration: 2, text: 't2' });
    graduateToolCall('call-2');
    markIterationTextPersisted(t, 2);

    emitIterationStarted(3);
    t.requestId = 'run-3';
    pushToolSeg(t, 'call-3');
    addCard(t, 'call-3', { status: 'running', resultText: '' });

    const vm = useTimelineViewModel();
    const segs = vm.liveInclusiveSegs.value as ToolSeg[];
    expect(segs.filter((s) => s.kind === 'tool').map((s) => String(s.card?.callId || ''))).toEqual(['call-1', 'call-2', 'call-3']);
    expect(segs.map((s) => s.kind)).toEqual(['tool', 'text', 'tool', 'text', 'tool']);
    expect(segs.filter((s) => s.kind !== 'tool').map((s) => (s as unknown as { text: string }).text)).toEqual(['t1', 't2']);
    expect(segs.filter((s) => s.kind === 'tool').map((s) => s.card?.resultText)).toEqual(['r1', 'r2', '']);
  });

  it('同调用快照首见为准，后续重复剪卡不覆盖终态', () => {
    const t = ensureTurn(SID);
    t.curIteration = 1;
    pushToolSeg(t, 'call-dup');
    addCard(t, 'call-dup', { status: 'done', resultText: 'first' });
    graduateToolCall('call-dup');
    emitIterationStarted(2);

    t.curIteration = 2;
    t.toolCards.push({
      callId: 'call-dup',
      functionName: 'search',
      argsJson: '{"q":"1"}',
      status: 'done',
      resultText: 'second',
      exitCode: null,
      durationMs: null,
      iteration: 2,
      receivedBytes: 8,
      persisted: true,
    });
    emitIterationStarted(3);

    expect(t.graduatedCards.filter((g) => g.callId === 'call-dup')).toHaveLength(1);
    expect(t.graduatedCards.find((g) => g.callId === 'call-dup')?.resultText).toBe('first');
  });
});
