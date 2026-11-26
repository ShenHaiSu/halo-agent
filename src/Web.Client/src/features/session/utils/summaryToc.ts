import type { TurnSummary } from '@shared/bridge/hostBridge';
import { compactTurnLabel, isCompactTurn } from './compactTurn';
import type { TocItem } from './historyToc';
import { flatBlocks, processBoxLabel, summaryAnchorKeys } from './timelineSummary';

export type TocRoleFilter = 'all' | 'user' | 'assistant';

export function buildSummaryToc(summaries: TurnSummary[], role: TocRoleFilter): TocItem[] {
  const out: TocItem[] = [];
  flatBlocks(summaries).forEach((turn, order) => {
    const round = order + 1;
    if (isCompactTurn(turn)) {
      if (role !== 'all') return;
      out.push({
        anchorKey: turn.turnId,
        role: 'compact',
        label: compactTurnLabel(turn.removedMessages),
        fullText: turn.userContent || turn.userPreview,
        charCount: Array.from(turn.userContent || turn.userPreview || '').length,
        index: round,
        round,
        toolCount: 0,
        hasReasoning: false,
        createdAtUtc: '',
        isRoundHead: false,
        isRoundTail: false,
      });
      return;
    }

    const anchors = summaryAnchorKeys(turn.turnId, turn.finalBlockId);
    if (role === 'all' || role === 'user') {
      out.push({
        anchorKey: anchors.user,
        role: 'user',
        label: turn.userPreview || '提问',
        fullText: turn.userContent || turn.userPreview,
        charCount: Array.from(turn.userContent || turn.userPreview || '').length,
        index: round,
        round,
        toolCount: 0,
        hasReasoning: false,
        createdAtUtc: '',
        isRoundHead: false,
        isRoundTail: false,
      });
    }
    if (role === 'all' || role === 'assistant') {
      out.push({
        anchorKey: anchors.process,
        role: 'assistant',
        label: `过程 · ${processBoxLabel(turn)}`,
        fullText: `过程内容 · ${processBoxLabel(turn)}`,
        charCount: turn.chars,
        index: round,
        round,
        toolCount: turn.tools,
        hasReasoning: false,
        createdAtUtc: '',
        isRoundHead: true,
        isRoundTail: false,
      });
      out.push({
        anchorKey: anchors.final,
        role: 'assistant',
        label: turn.finalPreview ? Array.from(turn.finalPreview).slice(0, 15).join('') : `本轮无正文 · ${turn.tools} 工具`,
        fullText: turn.finalPreview,
        charCount: Array.from(turn.finalPreview || '').length,
        index: round,
        round,
        toolCount: 0,
        hasReasoning: false,
        createdAtUtc: '',
        isRoundHead: false,
        isRoundTail: true,
      });
    }
  });
  return out;
}
