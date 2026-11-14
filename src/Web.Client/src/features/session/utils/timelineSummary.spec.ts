import { describe, expect, it } from 'vitest';
import { flatBlocks, processBoxLabel, statusBadge, summaryAnchorKeys, toFlatTurn } from './timelineSummary';
import type { TurnSummary } from '@shared/bridge/hostBridge';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: 'hello',
    finalBlockId: 'b3',
    finalPreview: 'final',
    blocks: 3,
    tools: 1,
    failed: 0,
    durationMs: 0,
    chars: 10,
    status: 'completed',
    lastRequestId: 'req',
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
    anchorKey: 'uturn-u1',
    ...over,
  } as TurnSummary;
}

describe('timelineSummary flatBlocks', () => {
  it('sorts by turnSeq and maps counts straight from backend rows', () => {
    const b = makeSummary({ turnId: 'uturn-b', turnSeq: 2, tools: 2, blocks: 5 });
    const a = makeSummary({ turnId: 'uturn-a', turnSeq: 1, tools: 0, blocks: 1 });
    const out = flatBlocks([b, a]);
    expect(out.map((t) => t.turnId)).toEqual(['uturn-a', 'uturn-b']);
    expect(out[1].tools).toBe(2);
  });

  it('empty tool round keeps zero final and empty-state wording', () => {
    const s = makeSummary({ finalBlockId: null, finalPreview: '', tools: 3, blocks: 3 });
    const flat = toFlatTurn(s);
    expect(flat.finalBlockId).toBeNull();
    expect(flat.tools).toBe(3);
  });

  it('status badges match ended states', () => {
    expect(statusBadge('completed')).toBe('✅');
    expect(statusBadge('truncated')).toBe('⏸');
    expect(statusBadge('cancelled')).toBe('⏹');
    expect(statusBadge('error')).toBe('❌');
  });

  it('process box label shows backend counts', () => {
    const flat = toFlatTurn(makeSummary({ blocks: 4, tools: 2, failed: 1 }));
    expect(processBoxLabel(flat)).toContain('4 块');
    expect(processBoxLabel(flat)).toContain('2 工具');
    expect(processBoxLabel(flat)).toContain('1 失败');
  });
});

describe('summaryAnchorKeys', () => {
  it('maps user, process and final anchors from backend rows', () => {
    expect(summaryAnchorKeys('uturn-u1', 'b3')).toEqual({
      user: 'uturn-u1',
      process: 'uturn-u1:process',
      final: 'b3',
    });
  });

  it('falls back to turn final anchor for pure tool rounds', () => {
    expect(summaryAnchorKeys('uturn-u9', null)).toEqual({
      user: 'uturn-u9',
      process: 'uturn-u9:process',
      final: 'uturn-u9:final',
    });
  });
});

describe('toFlatTurn user content', () => {
  it('passes full user content through for history body', () => {
    const full = '请帮我分析报错日志并给出修复步骤：' + 'a'.repeat(100);
    const flat = toFlatTurn(makeSummary({ userPreview: full.slice(0, 15), userContent: full }));
    expect(flat.userContent).toBe(full);
    expect(flat.userPreview).toBe(full.slice(0, 15));
  });

  it('falls back to preview when content is missing from older backend', () => {
    const flat = toFlatTurn(makeSummary({ userPreview: 'hello' }));
    expect(flat.userContent).toBe('hello');
  });
});
