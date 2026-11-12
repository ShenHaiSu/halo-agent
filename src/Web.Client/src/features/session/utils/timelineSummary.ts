import type { TurnSummary } from '@shared/bridge/hostBridge';

export interface FlatTimelineTurn {
  key: string;
  turnId: string;
  turnSeq: number;
  userPreview: string;
  finalPreview: string;
  finalBlockId: string | null;
  blocks: number;
  tools: number;
  failed: number;
  durationMs: number;
  chars: number;
  status: string;
  lastRequestId: string;
  anchorKey: string;
}

export function toFlatTurn(summary: TurnSummary): FlatTimelineTurn {
  const turnId = String(summary.turnId || '');
  return {
    key: turnId,
    turnId,
    turnSeq: Number(summary.turnSeq || 0),
    userPreview: String(summary.userPreview || ''),
    finalPreview: String(summary.finalPreview || ''),
    finalBlockId: (summary.finalBlockId as string | null | undefined) ?? null,
    blocks: Number(summary.blocks || 0),
    tools: Number(summary.tools || 0),
    failed: Number(summary.failed || 0),
    durationMs: Number(summary.durationMs || 0),
    chars: Number(summary.chars || 0),
    status: String(summary.status || 'completed'),
    lastRequestId: String(summary.lastRequestId || ''),
    anchorKey: String(summary.anchorKey || turnId),
  };
}

export function flatBlocks(summaries: TurnSummary[] | null | undefined): FlatTimelineTurn[] {
  const list = Array.isArray(summaries) ? summaries : [];
  return list
    .map(toFlatTurn)
    .filter((t) => t.turnId)
    .sort((a, b) => a.turnSeq - b.turnSeq);
}

export function statusBadge(status: string): string {
  switch (String(status || '').toLowerCase()) {
    case 'truncated':
      return '⏸';
    case 'cancelled':
      return '⏹';
    case 'error':
      return '❌';
    default:
      return '✅';
  }
}

export function processBoxLabel(turn: FlatTimelineTurn): string {
  const parts: string[] = [];
  if (turn.blocks > 0) parts.push(`${turn.blocks} 块`);
  if (turn.tools > 0) parts.push(`${turn.tools} 工具`);
  if (turn.failed > 0) parts.push(`${turn.failed} 失败`);
  if (turn.durationMs > 0) parts.push(`${Math.round(turn.durationMs / 1000)}s`);
  return parts.join(' · ') || '过程';
}
