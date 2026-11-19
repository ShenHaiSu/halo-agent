/** 摘要轮次锚点匹配：turnId → 完整投影条目的命中与兜底单测。 */
import { describe, expect, it } from 'vitest';
import { findTurnEntry } from './turnAnchorMatch';
import type { TimelineEntry } from './turnGroup';
import type { UserTurn } from './userTurn';

function turnEntry(over: Partial<UserTurn> = {}, key = 'uturn-u1'): TimelineEntry {
  return {
    key,
    kind: 'user-turn',
    turn: { key, userId: 'u1', requestId: 'req-1', steps: [], ...over } as unknown as UserTurn,
  };
}

describe('摘要轮次锚点匹配', () => {
  it('turnId 与投影 key 同值时命中完整条目', () => {
    const entry = turnEntry();
    expect(findTurnEntry([entry], 'uturn-u1')).toBe(entry);
  });

  it('turnId 等于 turn.key 时命中', () => {
    const entry = turnEntry({}, 'custom-key');
    expect(findTurnEntry([entry], 'custom-key')).toBe(entry);
  });

  it('用户消息 id 去前缀兜底命中', () => {
    const entry = turnEntry({ key: 'alias-key', userId: 'plain-user' }, 'alias-key');
    expect(findTurnEntry([entry], 'uturn-plain-user')).toBe(entry);
  });

  it('请求 id 兜底命中', () => {
    const entry = turnEntry({ key: 'k', userId: 'x', requestId: 'req-special' }, 'k');
    expect(findTurnEntry([entry], 'req-special')).toBe(entry);
  });

  it('无匹配返回 null', () => {
    expect(findTurnEntry([turnEntry()], 'uturn-other')).toBeNull();
  });

  it('跳过非 user-turn 条目', () => {
    const system = { key: 'uturn-u1', kind: 'system', msg: {} } as unknown as TimelineEntry;
    expect(findTurnEntry([system], 'uturn-u1')).toBeNull();
  });

  it('空 turnId 返回 null', () => {
    expect(findTurnEntry([turnEntry()], '')).toBeNull();
  });
});
