/** 稳态内容判定：只有确有可展示载荷才算有内容。 */
import { describe, expect, it } from 'vitest';
import { steadyTurnHasContent } from '@features/agent/utils/steadyReady';
import type { UserTurn } from '@features/session/utils/userTurn';

function turnWith(steps: Array<Record<string, unknown>>): UserTurn {
  return { steps } as unknown as UserTurn;
}

describe('SteadyContent_RequiresActualPayload', () => {
  it('null / 无 steps / 空 content / 空白 → 无内容', () => {
    expect(steadyTurnHasContent(null)).toBe(false);
    expect(steadyTurnHasContent(undefined)).toBe(false);
    expect(steadyTurnHasContent(turnWith([]))).toBe(false);
    expect(steadyTurnHasContent(turnWith([{ content: '' }]))).toBe(false);
    expect(steadyTurnHasContent(turnWith([{ content: '   ' }]))).toBe(false);
  });

  it('末段正文非空 → 有内容', () => {
    expect(steadyTurnHasContent(turnWith([{ content: '' }, { content: '结论' }]))).toBe(true);
  });

  it('任一步正文非空 → 有内容', () => {
    expect(steadyTurnHasContent(turnWith([{ content: '第一段' }, { content: '' }]))).toBe(true);
  });

  it('任一步思考非空 → 有内容', () => {
    expect(steadyTurnHasContent(turnWith([{ content: '', reasoning: '让我想想' }]))).toBe(true);
  });

  it('任一步含工具回包 → 有内容', () => {
    expect(steadyTurnHasContent(turnWith([{ content: '', toolMessages: [{ id: 't1' }] }]))).toBe(true);
  });

  it('空工具数组不算内容', () => {
    expect(steadyTurnHasContent(turnWith([{ content: '', toolMessages: [] }]))).toBe(false);
  });
});
