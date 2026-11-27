import { describe, expect, it } from 'vitest';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { summaryAnchorKeys, summaryAssistantText, summaryCopyText, summaryHistoryAnchors, summaryUserText } from './summaryMenu';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    userContent: '用户全文',
    userPreview: '用户预览',
    finalBlockId: 'b-final',
    finalPreview: '正文预览',
    userMessageId: 'u1',
    assistantMessageId: 'a2',
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

  it('复制此提问仅含用户单条，不含正文', () => {
    expect(summaryUserText(makeSummary())).toBe('用户全文');
    expect(summaryUserText(makeSummary())).not.toContain('正文块全文');
    expect(summaryUserText(null)).toBe('');
  });

  it('复制此回复仅含正文块，回落预览', () => {
    expect(summaryAssistantText(makeSummary(), '# 标题\n\n正文块全文')).toBe('# 标题\n\n正文块全文');
    expect(summaryAssistantText(makeSummary(), '')).toBe('正文预览');
    expect(summaryAssistantText(makeSummary(), '')).not.toContain('用户全文');
  });

  it('复制整轮问答为单条拼接（回归 B 方案口径）', () => {
    const turn = summaryCopyText(makeSummary({ finalPreview: '' }), '# 标题\n\n正文块全文');
    expect(turn).toBe(`用户全文\n\n# 标题\n\n正文块全文`);
  });

  it('空摘要时直接返回块文本裁边', () => {
    expect(summaryCopyText(null, '  正文  ')).toBe('正文');
  });

  it('历史锚点只取后端投影的真实消息 ID', () => {
    expect(summaryHistoryAnchors(makeSummary())).toEqual({ userId: 'u1', assistantId: 'a2' });
    expect(summaryHistoryAnchors(makeSummary({ userMessageId: null, assistantMessageId: null }))).toEqual({ userId: '', assistantId: '' });
  });

  it('导航锚点仍用 turnId 与 finalBlockId，不与操作锚点混用', () => {
    expect(summaryAnchorKeys(makeSummary())).toEqual({ turnId: 'uturn-u1', blockId: 'b-final' });
  });
});
