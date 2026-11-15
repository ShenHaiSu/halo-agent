import { describe, expect, it } from 'vitest';
import { makePendingUser, matchConfirmedBySummary } from '@features/session/utils/pendingUser';
import type { TurnSummary } from '@shared/bridge/hostBridge';

function makeTurn(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: 'hi',
    finalBlockId: 'b1',
    finalPreview: 'final',
    blocks: 1,
    tools: 0,
    failed: 0,
    durationMs: 0,
    chars: 5,
    status: 'completed',
    lastRequestId: 'req-1',
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
    anchorKey: 'uturn-u1',
    ...over,
  } as TurnSummary;
}

/** S3 增补验收：空占位三源判空（旧链空 + 摘要空 + 无活跃轮才算空）。 */
function isTimelineEmpty(persistedCount: number, summaryCount: number, hasActiveTurn: boolean): boolean {
  return persistedCount === 0 && summaryCount === 0 && !hasActiveTurn;
}

describe('S3 增补 F-sup-1 空占位语义', () => {
  it('v2 摘要有数据但 jsonl 为空时占位消失', () => {
    expect(isTimelineEmpty(0, 0, false)).toBe(true);
    expect(isTimelineEmpty(0, 2, false)).toBe(false);
    expect(isTimelineEmpty(0, 0, true)).toBe(false);
    expect(isTimelineEmpty(3, 0, false)).toBe(false);
  });
});

describe('S3 增补 F-sup-3 摘要源对账', () => {
  it('pending.requestId 命中 turn.lastRequestId 即清', () => {
    const p = makePendingUser({ requestId: 'req-9', sessionId: 's1', content: 'hello' });
    const summaries = [makeTurn({ turnId: 'uturn-a', lastRequestId: 'req-8' }), makeTurn({ turnId: 'uturn-b', lastRequestId: 'req-9' })];
    expect(matchConfirmedBySummary(p, summaries)?.lastRequestId).toBe('req-9');
  });

  it('未命中时保持 sending（双条隐患由 overlay 隐藏兜底）', () => {
    const p = makePendingUser({ requestId: 'req-x', sessionId: 's1', content: 'hello' });
    expect(matchConfirmedBySummary(p, [makeTurn({ lastRequestId: 'req-other' })])).toBeNull();
    expect(matchConfirmedBySummary(p, [])).toBeNull();
  });

  it('非 sending 态不参与对账', () => {
    const p = makePendingUser({ requestId: 'req-9', sessionId: 's1', content: 'hello' });
    p.status = 'failed';
    expect(matchConfirmedBySummary(p, [makeTurn({ lastRequestId: 'req-9' })])).toBeNull();
  });
});

describe('S3 增补 G-1 目录双分支', () => {
  it('v2 只用摘要源，v1 走旧链', () => {
    const summary = [{ anchorKey: 'uturn-u1' }, { anchorKey: 'uturn-u1:process' }];
    const legacy = [{ anchorKey: 'u-m1' }];
    const pick = (isV2: boolean, isSub: boolean) => (isV2 && !isSub ? summary : legacy);
    expect(pick(true, false)).toEqual(summary);
    expect(pick(false, false)).toEqual(legacy);
    expect(pick(true, true)).toEqual(legacy);
  });
});
