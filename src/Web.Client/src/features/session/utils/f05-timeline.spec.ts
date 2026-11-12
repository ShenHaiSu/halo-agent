/** 用户轮投影 + 目录纯函数单测（buildUserTurns + historyToc）。 */
import { describe, expect, it } from 'vitest';
import { buildUserTurns, type UserTurn } from '@features/session/utils/userTurn';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import { buildHistoryToc, bubbleLabel, labelOfEntry, toDisplayText } from '@features/session/utils/historyToc';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

function msg(partial: Partial<UnifiedChatMessage> & { id: string; role: string }): UnifiedChatMessage {
  return { content: '', createdAtUtc: '', ...partial } as UnifiedChatMessage;
}

function turnOf(e: TimelineEntry): UserTurn {
  return (e as unknown as { turn: UserTurn }).turn;
}

function assistantWithTools(id: string, callId: string): UnifiedChatMessage {
  return msg({
    id,
    role: 'assistant',
    content: '答',
    toolCalls: [{ id: callId, function: { name: 'bash' } }],
  } as unknown as Partial<UnifiedChatMessage> & { id: string; role: string });
}

describe('buildUserTurns 轮切分', () => {
  it('assistant+tool 归入同一用户轮', () => {
    const entries = buildUserTurns([
      msg({ id: 'u1', role: 'user', content: 'hi' }),
      assistantWithTools('a1', 'c1'),
      msg({ id: 't1', role: 'tool', content: 'out', toolCallId: 'c1' } as unknown as Partial<UnifiedChatMessage> & {
        id: string;
        role: string;
      }),
    ]);
    expect(entries).toHaveLength(1);
    expect(entries[0]?.key).toBe('uturn-u1');
    const turn = turnOf(entries[0]!);
    expect(turn.steps).toHaveLength(1);
    expect(turn.steps[0]?.toolMessages).toHaveLength(1);
  });
  it('孤立 tool 自成一轮（无 user 头）', () => {
    const entries = buildUserTurns([msg({ id: 't9', role: 'tool', content: 'out' })]);
    expect(entries).toHaveLength(1);
    expect(turnOf(entries[0]!).userMsg).toBeNull();
  });
  it('user 截断当前轮', () => {
    const entries = buildUserTurns([
      msg({ id: 'u1', role: 'user', content: '问一' }),
      msg({ id: 'a1', role: 'assistant', content: '答一' }),
      msg({ id: 'u2', role: 'user', content: '问二' }),
      msg({ id: 'a2', role: 'assistant', content: '答二' }),
    ]);
    expect(entries.map((e) => e.key)).toEqual(['uturn-u1', 'uturn-u2']);
  });
});

describe('historyToc', () => {
  it('toDisplayText 清洗 markdown/链接/HTML', () => {
    expect(toDisplayText('# 标题\n\n正文 **加粗** [链接](http://a)')).toBe('标题 正文 加粗 链接');
    expect(toDisplayText('![图](a.png)')).toBe('[图片]');
    expect(toDisplayText('<script>alert(1)</script>hi')).toContain('hi');
  });
  it('bubbleLabel 超 15 字截断加省略号', () => {
    expect(bubbleLabel('你好世界，这是一段超过十五个字的中文文本')).toBe('你好世界，这是一段超过十五个字…');
    expect(bubbleLabel('短文本')).toBe('短文本');
  });
  it('buildHistoryToc 用户轮按轮列目录', () => {
    const entries = buildUserTurns([
      msg({ id: 'u1', role: 'user', content: '第一问是什么' }),
      msg({ id: 'a1', role: 'assistant', content: '第一答内容' }),
      msg({ id: 'u2', role: 'user', content: '第二问是什么' }),
      msg({ id: 'a2', role: 'assistant', content: '第二答内容' }),
      msg({ id: 'a3', role: 'assistant', content: '第三答内容' }),
    ]);
    expect(entries).toHaveLength(2);
    const toc = buildHistoryToc(entries, { role: 'all', roundHeadsOnly: false });
    expect(toc).toHaveLength(2);
    expect(toc.every((t) => t.role === 'user')).toBe(true);
    expect(String(labelOfEntry(entries[0]!))).toContain('第一问是什么');
  });
  it('孤立 tool 旧轮条目 label 带扳手（子会话兼容 kind=turn）', () => {
    const toolMsg = msg({ id: 't1', role: 'tool', content: 'out' });
    const toolEntry = {
      key: 'tool-t1',
      kind: 'turn',
      group: {
        key: 'tool-t1',
        assistantId: '',
        reasoning: null,
        content: null,
        toolCalls: [],
        toolMessages: [toolMsg],
        telemetry: null,
        createdAtUtc: '',
      },
    } as unknown as TimelineEntry;
    const toc = buildHistoryToc([toolEntry], { role: 'all', roundHeadsOnly: false });
    expect(toc[0]?.role).toBe('assistant');
    expect(String(toc[0]?.label || '').startsWith('🔧')).toBe(true);
    expect(labelOfEntry(toolEntry)).toContain('🔧');
  });
});
