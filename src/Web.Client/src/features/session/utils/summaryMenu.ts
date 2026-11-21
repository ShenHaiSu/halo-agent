import type { TurnSummary } from '@shared/bridge/hostBridge';
import { trimBlankEdges } from '@shared/utils/trimBlank';

/** 摘要源单条用户文本：仅用户提问，不含正文。 */
export function summaryUserText(summary: TurnSummary | null | undefined): string {
  if (!summary) return '';
  return trimBlankEdges(String(summary.userContent || summary.userPreview || ''));
}

/** 摘要源单条助手文本：仅最终正文块（markdown 原文），回落 finalPreview。 */
export function summaryAssistantText(summary: TurnSummary | null | undefined, blocksMarkdown: string): string {
  const body = trimBlankEdges(blocksMarkdown);
  if (body) return body;
  if (!summary) return '';
  return trimBlankEdges(String(summary.finalPreview || ''));
}

/** 摘要源整轮复制文本：用户全文 + 正文块全文，阅读序；过程块默认排除。 */
export function summaryCopyText(summary: TurnSummary | null | undefined, blocksMarkdown: string): string {
  if (!summary) return trimBlankEdges(blocksMarkdown);
  const parts: string[] = [];
  const userText = trimBlankEdges(String(summary.userContent || summary.userPreview || ''));
  if (userText) parts.push(userText);
  const body = trimBlankEdges(blocksMarkdown);
  if (body) parts.push(body);
  const finalPreview = trimBlankEdges(String(summary.finalPreview || ''));
  if (finalPreview && !body.includes(finalPreview.slice(0, 40))) parts.push(finalPreview);
  return parts.map((p) => p.replace(/\n+$/, '')).join('\n\n');
}

/** 摘要源锚点：用户锚用 turnId，正文锚优先 finalBlockId。 */
export function summaryAnchorIds(summary: TurnSummary | null | undefined): { userId: string; assistantId: string } {
  const turnId = String(summary?.turnId || '').trim();
  const finalBlockId = String(summary?.finalBlockId || '').trim();
  return { userId: turnId, assistantId: finalBlockId || turnId };
}
