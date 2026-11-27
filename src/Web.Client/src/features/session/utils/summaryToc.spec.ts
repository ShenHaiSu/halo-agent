import { describe, expect, it } from 'vitest';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { buildSummaryToc } from './summaryToc';

function summary(over: Partial<TurnSummary>): TurnSummary {
  return {
    turnId: 'turn-normal-a',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: '普通提问',
    userContent: '普通提问全文',
    finalBlockId: 'block-final-a',
    finalPreview: '普通回复',
    blocks: 2,
    tools: 1,
    failed: 0,
    durationMs: 100,
    chars: 20,
    status: 'completed',
    lastRequestId: '',
    createdAtUtc: '',
    updatedAtUtc: '',
    anchorKey: 'turn-normal-a',
    ...over,
  } as TurnSummary;
}

const summaries = [
  summary({}),
  summary({
    turnId: 'turn-compact-a',
    turnSeq: 2,
    kind: 'compact',
    userPreview: '上下文压缩',
    userContent: '压缩摘要正文',
    finalBlockId: 'legacy-compact-block',
    finalPreview: '不得进入目录',
    blocks: 1,
    tools: 1,
    removedMessages: 18,
  }),
];

describe('buildSummaryToc', () => {
  it('全部角色保留普通轮三项与 compact 一项', () => {
    const items = buildSummaryToc(summaries, 'all');
    expect(items).toHaveLength(4);
    const compact = items.filter((item) => item.role === 'compact');
    assertSingleCompact(compact, summaries[1]);
    expect(items.map((item) => item.round)).toEqual([1, 1, 1, 2]);
  });

  it('user 与 assistant 过滤不显示 compact', () => {
    expect(buildSummaryToc(summaries, 'user').some((item) => item.role === 'compact')).toBe(false);
    expect(buildSummaryToc(summaries, 'assistant').some((item) => item.role === 'compact')).toBe(false);
  });

  it('compact 不生成过程或 final 假项', () => {
    const compact = buildSummaryToc(summaries, 'all').find((item) => item.role === 'compact');
    expect(compact?.anchorKey).toBe('turn-compact-a');
    expect(compact?.label).toContain('18 条');
    expect(compact?.fullText).toBe('压缩摘要正文');
    expect(buildSummaryToc(summaries, 'all').some((item) => item.anchorKey.includes(':process') && item.round === 2)).toBe(false);
    expect(buildSummaryToc(summaries, 'all').some((item) => item.anchorKey.includes(':final') && item.round === 2)).toBe(false);
  });
});

function assertSingleCompact(items: ReturnType<typeof buildSummaryToc>, compact: TurnSummary): void {
  expect(items).toHaveLength(1);
  expect(items[0].anchorKey).toBe(compact.turnId);
}
