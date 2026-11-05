import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useMessagesStore } from './messages';
import { useStreamStore } from './stream';
import { dispatchFrames } from './eventBridge';
import type { HaloFrame } from '@/bridge/types';

function frames(list: Array<{ seq: number; k: string; p: unknown }>): HaloFrame[] {
  return list.map((f) => ({ t: 'evt', ...f }) as unknown as HaloFrame);
}

function setupRunningTool(tid: string, tcid: string): void {
  const messages = useMessagesStore();
  dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: tid } }]));
  dispatchFrames(
    frames([{ seq: 2, k: 'tool.started', p: { toolCallId: tcid, name: 'run_shell', args: '{}', used: 1 } }]),
  );
  expect(messages.turns.find((x) => x.turnId === tid)!.final.toolCalls?.some((c) => c.id === tcid)).toBe(true);
}

beforeEach(() => {
  setActivePinia(createPinia());
});

describe('eventBridge byUser/cancelled', () => {
  it('F3: turn.finished{byUser:true} → truncatedByUser 且互斥', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(
      frames([{ seq: 2, k: 'turn.finished', p: { turnId: 't1', text: '⏹ 生成已中断', truncated: true, byUser: true, finalUsed: 1 } }]),
    );
    const t = messages.turns.find((x) => x.turnId === 't1')!;
    expect(t.final.truncatedByUser).toBe(true);
    expect(t.final.truncatedByLength).toBe(false);
    expect(stream.currentTurn).toBeNull();
  });

  it('F3b: turn.finished{truncated:true} 无 byUser → truncatedByLength', () => {
    const messages = useMessagesStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(
      frames([{ seq: 2, k: 'turn.finished', p: { turnId: 't1', text: 'x', truncated: true, finalUsed: 1 } }]),
    );
    const t = messages.turns.find((x) => x.turnId === 't1')!;
    expect(t.final.truncatedByLength).toBe(true);
    expect(t.final.truncatedByUser).not.toBe(true);
  });

  it('F5: 取消中到达的 tool.finished{ok:false} → cancelled', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    setupRunningTool('t1', 'tc1');
    stream.beginCancel();
    dispatchFrames(frames([{ seq: 3, k: 'tool.finished', p: { toolCallId: 'tc1', name: 'run_shell', ok: false, output: 'x', used: 1 } }]));
    const tc = messages.turns.find((x) => x.turnId === 't1')!.final.toolCalls!.find((c) => c.id === 'tc1')!;
    expect(tc.status).toBe('cancelled');
  });

  it('F5b: 未取消时 tool.finished{ok:false} → error（回归）', () => {
    const messages = useMessagesStore();
    setupRunningTool('t1', 'tc1');
    dispatchFrames(frames([{ seq: 3, k: 'tool.finished', p: { toolCallId: 'tc1', name: 'run_shell', ok: false, output: 'boom', used: 1 } }]));
    const tc = messages.turns.find((x) => x.turnId === 't1')!.final.toolCalls!.find((c) => c.id === 'tc1')!;
    expect(tc.status).toBe('error');
  });

  it('F5c: beginCancel 之前已定为 error 的工具，兜底不翻成 cancelled', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    setupRunningTool('t1', 'tc1');
    // 先到达（未取消）→ error
    dispatchFrames(frames([{ seq: 3, k: 'tool.finished', p: { toolCallId: 'tc1', name: 'run_shell', ok: false, output: 'boom', used: 1 } }]));
    // 后取消 → turn.finished 兜底只改 running/pending，不动 error
    stream.beginCancel();
    dispatchFrames(
      frames([{ seq: 4, k: 'turn.finished', p: { turnId: 't1', text: '⏹ 生成已中断', truncated: true, byUser: true, finalUsed: 1 } }]),
    );
    const tc = messages.turns.find((x) => x.turnId === 't1')!.final.toolCalls!.find((c) => c.id === 'tc1')!;
    expect(tc.status).toBe('error');
  });

  it('F6: turn.finished{byUser:true} 把仍 running 的工具改 cancelled', () => {
    const messages = useMessagesStore();
    setupRunningTool('t1', 'tc1');
    dispatchFrames(
      frames([{ seq: 3, k: 'turn.finished', p: { turnId: 't1', text: '⏹ 生成已中断', truncated: true, byUser: true, finalUsed: 1 } }]),
    );
    const tc = messages.turns.find((x) => x.turnId === 't1')!.final.toolCalls!.find((c) => c.id === 'tc1')!;
    expect(tc.status).toBe('cancelled');
  });
});
