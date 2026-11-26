/** 摘要源目录项：每轮三锚点（提问/过程/末轮），跳转口径与摘要区一致。 */
export interface TocItem {
  anchorKey: string;
  role: 'user' | 'assistant' | 'system' | 'compact';
  label: string;
  fullText: string;
  charCount: number;
  index: number;
  round: number;
  toolCount: number;
  hasReasoning: boolean;
  createdAtUtc: string;
  isRoundHead: boolean;
  isRoundTail: boolean;
}

/** 气泡标签截断：按字符数裁剪，避免超长标题撑开目录。 */
export function bubbleLabel(text: string, max = 60): string {
  const s = String(text || '');
  if (Array.from(s).length <= max) return s;
  return Array.from(s).slice(0, max).join('') + '…';
}
