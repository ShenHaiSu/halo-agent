import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { vi } from 'vitest';
vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});
import { describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushSegBuf, queueSegDelta } from './useAgentStream';
import { demotePending, ensureTurn, finalizePending, markIterationTextPersisted } from './model';

/**
 * 流式段的到达序保真：交错的正文/思考按事件到达先后入列、按同一顺序入 segs。
 * 定序真相源是"单一有序队列 + 按队列顺序入列"，不依赖 seq 排序，也不改 tag/毕业语义。
 */
describe('streaming segs keep arrival order', () => {
  it('StreamSegs_KeepArrivalOrderOnInterleave', () => {
    setActivePinia(createPinia());
    const sid = 'sess-order-interleave';
    const t = ensureTurn(sid);
    t.curIteration = 1;
    t.requestId = 'req-order';
    t.segs = [];

    // 同一帧内到达：reasoning(seq1) → text(seq2) → reasoning(seq3)，同一 blockId。
    queueSegDelta(sid, 'reasoning', 'R1', 'b1', 1, 'process');
    queueSegDelta(sid, 'text', 'T1', 'b1', 2, 'pending');
    queueSegDelta(sid, 'reasoning', 'R2', 'b1', 3, 'process');
    flushSegBuf(sid);

    expect(t.segs.map((s) => s.kind)).toEqual(['reasoning', 'text', 'reasoning']);
    expect(t.segs.map((s) => s.text)).toEqual(['R1', 'T1', 'R2']);
    expect(t.segs.map((s) => s.seq)).toEqual([1, 2, 3]);
  });

  it('StreamSegs_SplitOnBlockIdChange', () => {
    setActivePinia(createPinia());
    const sid = 'sess-order-blockid';
    const t = ensureTurn(sid);
    t.curIteration = 1;
    t.requestId = 'req-block';
    t.segs = [];

    // kind 交错 + blockId 切换：三段各自成段，交错不丢，顺序保持到达序。
    queueSegDelta(sid, 'text', 'A', 'b1', 1, 'pending');
    queueSegDelta(sid, 'reasoning', 'R', 'b1', 1, 'process');
    queueSegDelta(sid, 'text', 'B', 'b2', 2, 'pending');
    flushSegBuf(sid);

    expect(t.segs.map((s) => s.kind)).toEqual(['text', 'reasoning', 'text']);
    expect(t.segs.map((s) => s.text)).toEqual(['A', 'R', 'B']);
    expect(t.segs.map((s) => s.blockId)).toEqual(['b1', 'b1', 'b2']);
  });

  it('StreamSegs_NoSortBySeq', () => {
    // 静态守卫：定序由到达序保证，改动文件不得对 segs/items 排序，也不得按 seq 重排。
    const file = resolve(process.cwd(), 'src/features/agent/stores/useAgentStream.ts');
    const src = readFileSync(file, 'utf8');
    expect(src).not.toMatch(/segs\.sort\s*\(/);
    expect(src).not.toMatch(/items\.sort\s*\(/);
    expect(src).not.toMatch(/\.sort\s*\([^)]*\.seq/);
  });

  it('StreamSegs_PreserveTagsAndTelemetry', () => {
    setActivePinia(createPinia());
    const sid = 'sess-order-tags';
    const t = ensureTurn(sid);
    t.curIteration = 1;
    t.requestId = 'req-tags';
    t.segs = [];
    const before = t.telemetry.liveEst;

    // tag 缺省语义：text → pending、reasoning → process；聚合串与 telemetry 累加保留。
    queueSegDelta(sid, 'text', 'hello world', 'b1', 1);
    queueSegDelta(sid, 'reasoning', 'think', 'b2', 2);
    flushSegBuf(sid);

    expect(t.streamingText).toBe('hello world');
    expect(t.streamingReasoning).toBe('think');
    expect(t.segs[0].tag).toBe('pending');
    expect(t.segs[1].tag).toBe('process');
    expect(t.telemetry.liveEst).toBeGreaterThan(before);
    expect(t.streamingActive).toBe(true);
    expect(t.isGenerating).toBe(true);

    // tag 毕业链在按序入列后仍完好（既有语义零改）。
    demotePending(t);
    expect(t.segs[0].tag).toBe('process');
    finalizePending(t);
    expect(t.segs.some((s) => s.tag === 'final')).toBe(true);
    markIterationTextPersisted(t, 1);
    expect(t.segs[0].persisted).toBe(true);
  });
});
