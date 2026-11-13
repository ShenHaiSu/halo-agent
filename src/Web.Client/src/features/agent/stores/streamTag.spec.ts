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

