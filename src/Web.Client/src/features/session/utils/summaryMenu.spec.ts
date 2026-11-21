import { describe, expect, it } from 'vitest';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { summaryAnchorIds, summaryCopyText } from './summaryMenu';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    userContent: '用户全文',
    userPreview: '用户预览',
    finalBlockId: 'b-final',
    finalPreview: '正文预览',
    ...over,
  } as TurnSummary;
}

describe('summaryMenuCopiesFullTurnWithoutEntryHack', () => {
  it('用户全文 + 块正文按阅读序拼接', () => {
    const text = summaryCopyText(makeSummary(), '# 标题\n\n正文块全文');
    expect(text).toContain('用户全文');
    expect(text).toContain('正文块全文');
    expect(text.indexOf('用户全文')).toBeLessThan(text.indexOf('正文块全文'));
  });

  it('空摘要时直接返回块文本裁边', () => {
    expect(summaryCopyText(null, '  正文  ')).toBe('正文');
  });

  it('锚点：用户锚用 turnId，正文锚优先 finalBlockId', () => {
    expect(summaryAnchorIds(makeSummary({ finalBlockId: '' }))).toEqual({ userId: 'uturn-u1', assistantId: 'uturn-u1' });
    expect(summaryAnchorIds(makeSummary())).toEqual({ userId: 'uturn-u1', assistantId: 'b-final' });
  });
});
