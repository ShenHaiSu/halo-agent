import { vi } from 'vitest';
vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});
import { handleTurnLifecycle } from './turnEvents';
import { describe, expect, it } from 'vitest';
import { ensureTurn, pushTextSeg, demotePending, finalizePending, markIterationTextPersisted } from './model';
import { createPinia, setActivePinia } from 'pinia';

describe('streaming tag mirror demote/finalize', () => {
  it('pending text demotes on iteration start and finalizes last non-empty', () => {
    setActivePinia(createPinia());
    const t = ensureTurn('sess-tag');
    t.curIteration = 1;
    t.requestId = 'req-tag';
    t.segs = [];
    pushTextSeg(t, 'text', 'hello', 'req-tag:b1', 1, 'pending');
    pushTextSeg(t, 'text', ' world', 'req-tag:b1', 1, 'pending');
    expect(t.segs.length).toBe(1);
    demotePending(t);
    expect(t.segs[0].tag).toBe('process');
    t.segs = [];
    pushTextSeg(t, 'text', 'first', 'req-tag:b2', 2, 'pending');
    pushTextSeg(t, 'text', 'second', 'req-tag:b3', 3, 'pending');
    const finalId = finalizePending(t);
    expect(finalId).toBe('req-tag:b3');
    const tags = t.segs.map((s) => s.tag);
    expect(tags).toContain('final');
    expect(tags).not.toContain('pending');
  });

  it('markIterationTextPersisted keeps tool segs live', () => {
    setActivePinia(createPinia());
    const t = ensureTurn('sess-persist');
    t.curIteration = 1;
    t.requestId = 'req-p';
    t.segs = [];
    pushTextSeg(t, 'text', 'a', 'b1', 1, 'pending');
    expect(t.segs.length).toBeGreaterThan(0);
    markIterationTextPersisted(t, 1);
    expect(t.segs[0].persisted).toBe(true);
  });
});

describe('BlockTagUpdated flip', () => {
  it('flips a live final seg back to process on flip reason', () => {
    setActivePinia(createPinia());
    const t = ensureTurn('sess-flip');
    t.curIteration = 1;
    t.requestId = 'req-flip';
    t.segs = [];
    pushTextSeg(t, 'text', 'old final', 'req-flip:b1', 1, 'pending');
    const finalId = finalizePending(t);
    expect(finalId).toBe('req-flip:b1');
    expect(t.segs[0].tag).toBe('final');
    handleTurnLifecycle('BlockTagUpdated', { blockId: finalId, tag: 'process', reason: 'flip' }, 'sess-flip');
    expect(t.segs[0].tag).toBe('process');
  });
  it('ignores flip for unknown block ids and invalid tags', () => {
    setActivePinia(createPinia());
    const t = ensureTurn('sess-flip-unknown');
    t.curIteration = 1;
    t.requestId = 'req-flip-u';
    t.segs = [];
    pushTextSeg(t, 'text', 'live', 'req-flip-u:b1', 1, 'pending');
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'req-flip-u:absent', tag: 'process', reason: 'flip' }, 'sess-flip-unknown');
    expect(t.segs[0].tag).toBe('pending');
    handleTurnLifecycle('BlockTagUpdated', { blockId: 'req-flip-u:b1', tag: 'pending', reason: 'flip' }, 'sess-flip-unknown');
    expect(t.segs[0].tag).toBe('pending');
  });
});
