import type { FlatTimelineTurn } from '@features/session/utils/timelineSummary';

/** 摘要轮是否确有可展示内容：用户文、过程块、工具数、正文预览任一非空。 */
export function steadyTurnHasContent(turn: FlatTimelineTurn | null | undefined): boolean {
  if (!turn) return false;
  if (String(turn.userContent || turn.userPreview || '').trim()) return true;
  if (String(turn.finalPreview || '').trim()) return true;
  if (Number(turn.blocks || 0) > 0) return true;
  if (Number(turn.tools || 0) > 0) return true;
  return false;
}
