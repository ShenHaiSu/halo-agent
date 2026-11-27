import { describe, expect, it } from 'vitest';
import { isPendingCancelled, makePendingUser, matchConfirmedBySummary } from '@features/session/utils/pendingUser';
import { isEmptyTerminalTurn, toFlatTurn } from '@features/session/utils/timelineSummary';
import type { TurnSummary } from '@shared/bridge/hostBridge';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-user-first',
    turnSeq: 1,
    iterEnd: 0,
    userPreview: 'first question',
    userContent: 'first question',
    finalBlockId: null,
    finalPreview: '',
    blocks: 0,
    tools: 0,
    failed: 0,
    durationMs: 0,
    chars: 0,
    status: 'cancelled',
    lastRequestId: 'request-cancel',
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
    anchorKey: 'uturn-user-first',
    ...over,
  } as TurnSummary;
}

describe('取消态纯函数', () => {
  it('cancelled 态可识别且不等同 failed', () => {
    const p = makePendingUser({ requestId: 'request-cancel', sessionId: 'sess-cancel', content: 'hi' });
    p.status = 'cancelled';
    expect(isPendingCancelled(p)).toBe(true);
    expect(isPendingCancelled(null)).toBe(false);
    expect(isPendingCancelled({ ...p, status: 'failed' })).toBe(false);
  });

  it('cancelled 态仍可按 requestId 精确转正', () => {
    const p = makePendingUser({ requestId: 'request-cancel', sessionId: 'sess-cancel', content: 'hi' });
    p.status = 'cancelled';
    expect(matchConfirmedBySummary(p, [{ lastRequestId: 'request-cancel' }])).not.toBeNull();
    expect(matchConfirmedBySummary(p, [{ lastRequestId: 'other' }])).toBeNull();
  });

  it('空 requestId 不误命中', () => {
    const p = makePendingUser({ requestId: '', sessionId: 'sess-cancel', content: 'hi' });
    p.status = 'cancelled';
    expect(matchConfirmedBySummary(p, [{ lastRequestId: '' }])).toBeNull();
  });
});

describe('零块终态轮判断', () => {
  it('零块 cancelled 轮命中紧凑展示', () => {
    expect(isEmptyTerminalTurn(toFlatTurn(makeSummary()))).toBe(true);
  });

  it('零块 error 轮命中紧凑展示', () => {
    expect(isEmptyTerminalTurn(toFlatTurn(makeSummary({ status: 'error' })))).toBe(true);
  });

  it('有过程块的取消轮不命中', () => {
    expect(isEmptyTerminalTurn(toFlatTurn(makeSummary({ blocks: 2, tools: 1 })))).toBe(false);
  });

  it('有正文的取消轮不命中', () => {
    expect(isEmptyTerminalTurn(toFlatTurn(makeSummary({ finalPreview: 'half answer' })))).toBe(false);
  });

  it('正常完成轮不命中', () => {
    expect(isEmptyTerminalTurn(toFlatTurn(makeSummary({ status: 'completed', blocks: 1 })))).toBe(false);
  });

  it('空输入不命中', () => {
    expect(isEmptyTerminalTurn(null)).toBe(false);
    expect(isEmptyTerminalTurn(undefined)).toBe(false);
  });
});
