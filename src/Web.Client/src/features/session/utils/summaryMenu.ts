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

/** 历史操作锚点：只取后端投影的真实消息 ID，禁止使用 turnId 或 finalBlockId。 */
export function summaryHistoryAnchors(summary: TurnSummary | null | undefined): { userId: string; assistantId: string } {
  return {
    userId: String(summary?.userMessageId || '').trim(),
    assistantId: String(summary?.assistantMessageId || '').trim(),
  };
}

/** 导航锚点：turnId / finalBlockId 只负责 UI 定位与展开，不参与历史操作。 */
export function summaryAnchorKeys(summary: TurnSummary | null | undefined): { turnId: string; blockId: string } {
  return {
    turnId: String(summary?.turnId || '').trim(),
    blockId: String(summary?.finalBlockId || '').trim(),
  };
}
