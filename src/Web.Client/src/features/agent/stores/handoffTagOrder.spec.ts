/** 流稳切换上游竞态：delta 缓冲与 tag 事件的到达顺序不得留下多条 final。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { ensureTurn, finalizePending, turns } from './model';
import { handleTurnLifecycle } from './turnEvents';
import { dropSegBuf, flushSegBufSync, queueSegDelta } from './useAgentStream';

const SESSION = 'sess-handoff-order';

beforeEach(() => {
  setActivePinia(createPinia());
  turns.value = {};
});

describe('轮末唯一 final 不变量', () => {
  it('finalizePending 先降级旧 final，全轮只剩最后一条非空正文', () => {
    const t = ensureTurn(SESSION);
    t.curIteration = 2;
    t.segs = [
      { key: 'txt-earlier', kind: 'text', iteration: 1, text: '早期正文', tag: 'final' },
      { key: 'txt-mid', kind: 'text', iteration: 1, text: '中段正文', tag: 'final' },
      { key: 'tool-a', kind: 'tool', iteration: 2, text: '', tag: 'process' },
      { key: 'txt-last', kind: 'text', iteration: 2, text: '末轮正文', tag: 'pending' },
    ];
    const finalId = finalizePending(t);
    expect(finalId).toBe('txt-last');
    expect(t.segs.filter((s) => s.tag === 'final').map((s) => s.key)).toEqual(['txt-last']);
    expect(t.segs.find((s) => s.key === 'txt-earlier')?.tag).toBe('process');
    expect(t.segs.find((s) => s.key === 'txt-mid')?.tag).toBe('process');
  });

  it('后到的 flip 把乐观 final 降级，仍只留一个 final', () => {
    const t = ensureTurn(SESSION);
    t.curIteration = 1;
    t.segs = [
      { key: 'txt-old', kind: 'text', iteration: 1, text: '旧正文', blockId: 'blk-old', tag: 'final' },
      { key: 'txt-new', kind: 'text', iteration: 1, text: '新正文', blockId: 'blk-new', tag: 'final' },
    ];
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'blk-old', tag: 'process', reason: 'flip' }, SESSION);
    expect(t.segs.map((s) => s.tag)).toEqual(['process', 'final']);
  });
});

describe('delta 缓冲与 tag 事件交错', () => {
  it('delta 还在 rAF 队列时收到 flip：先 flush 建 seg，再降级旧 final', () => {
    const t = ensureTurn(SESSION);
    t.curIteration = 1;
    t.segs = [{ key: 'txt-old', kind: 'text', iteration: 1, text: '旧正文', blockId: 'blk-old', tag: 'final' }];
    queueSegDelta(SESSION, 'text', '新正文', 'blk-new', 1, 'pending');
    // 未 flush 前 seg 尚未建成，flip 事件若直接遍历 segs 会打空。
    expect(t.segs.length).toBe(1);
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'blk-new', tag: 'final', reason: 'flip' }, SESSION);
    expect(t.segs.length).toBe(2);
    expect(t.segs.map((s) => s.tag)).toEqual(['process', 'final']);
    expect(t.segs[1].text).toBe('新正文');
  });

  it('flip 先于 delta 到达：override 在 seg 建成时消费', () => {
    const t = ensureTurn(SESSION);
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'blk-early', tag: 'process', reason: 'flip' }, SESSION);
    expect(t.pendingTagOverrides['blk-early']).toEqual({ tag: 'process', reason: 'flip' });
    queueSegDelta(SESSION, 'text', '迟到 delta 的正文', 'blk-early', 1, 'pending');
    flushSegBufSync(SESSION);
    expect(t.segs[0].tag).toBe('process');
    expect(t.pendingTagOverrides['blk-early']).toBeUndefined();
  });

  it('同一 blockId 后到的 finalize 覆盖旧 override，建 seg 时标 final', () => {
    const t = ensureTurn(SESSION);
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'blk-over', tag: 'process', reason: 'flip' }, SESSION);
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'blk-over', tag: 'final', reason: 'finalize' }, SESSION);
    expect(t.pendingTagOverrides['blk-over']?.tag).toBe('final');
    queueSegDelta(SESSION, 'text', '最终正文', 'blk-over', 1, 'pending');
    flushSegBufSync(SESSION);
    expect(t.segs[0].tag).toBe('final');
    expect(t.pendingTagOverrides['blk-over']).toBeUndefined();
  });

  it('override 命中 final 时也维持唯一性', () => {
    const t = ensureTurn(SESSION);
    t.segs = [{ key: 'txt-old', kind: 'text', iteration: 1, text: '旧 final', blockId: 'blk-old', tag: 'final' }];
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'blk-incoming', tag: 'final', reason: 'flip' }, SESSION);
    queueSegDelta(SESSION, 'text', '迟到的新 final', 'blk-incoming', 2, 'pending');
    flushSegBufSync(SESSION);
    expect(t.segs.filter((s) => s.tag === 'final').map((s) => s.text)).toEqual(['迟到的新 final']);
  });

  it('清桶丢弃未消费 override，切会话不串状态', () => {
    const t = ensureTurn(SESSION);
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'blk-dropped', tag: 'process', reason: 'demote' }, SESSION);
    expect(Object.keys(t.pendingTagOverrides).length).toBe(1);
    dropSegBuf(SESSION);
    expect(Object.keys(t.pendingTagOverrides).length).toBe(0);
  });
});
