import type { UserTurn } from '@features/session/utils/userTurn';
import { lastStepContent } from '@features/session/utils/userTurn';

/** 投影出的稳态轮是否确有可展示内容：末段正文，或任一步的正文/思考/工具回包。 */
export function steadyTurnHasContent(turn: UserTurn | null | undefined): boolean {
  if (!turn) return false;
  if (String(lastStepContent(turn) ?? '').trim()) return true;
  const steps = Array.isArray((turn as { steps?: unknown[] }).steps) ? (turn as { steps: any[] }).steps : [];
  for (const st of steps) {
    if (String((st as { content?: unknown }).content ?? '').trim()) return true;
    if (String((st as { reasoning?: unknown }).reasoning ?? '').trim()) return true;
    const tools = (st as { toolMessages?: unknown[] }).toolMessages;
    if (Array.isArray(tools) && tools.length > 0) return true;
  }
  return false;
}
