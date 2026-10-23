/** need21-F05: 时间轴/目录纯函数单测（turnGroup + historyToc）。 */
import { describe, expect, it } from 'vitest';
import { buildTimelineEntries, buildTurnGroups, type TurnGroup } from '@features/session/utils/turnGroup';
import { buildHistoryToc, bubbleLabel, labelOfEntry, toDisplayText } from '@features/session/utils/historyToc';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

function msg(partial: Partial<UnifiedChatMessage> & { id: string; role: string }): UnifiedChatMessage {
  return { content: '', createdAtUtc: '', ...partial } as UnifiedChatMessage;
}

describe('buildTurnGroups', () => {
  it('assistant+tool 归一组', () => {
    const groups = buildTurnGroups([
      msg({ id: 'u1', role: 'user', content: 'hi' }),
      msg({
        id: 'a1',
        role: 'assistant',
        content: '答',
        toolCalls: [{ id: 'c1', function: { name: 'bash' } }],
      } as unknown as Partial<UnifiedChatMessage> & { id: string; role: string }),
      msg({ id: 't1', role: 'tool', content: 'out', toolCallId: 'c1' } as unknown as Partial<UnifiedChatMessage> & {
        id: string;
        role: string;
      }),
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0]?.toolMessages).toHaveLength(1);
  });
  it('孤立 tool 自成一组', () => {
    const groups = buildTurnGroups([msg({ id: 't9', role: 'tool', content: 'out' })]);
    expect(groups).toHaveLength(1);
    expect(groups[0]?.key).toBe('tool-t9');
  });
  it('user 截断当前组', () => {
    const groups = buildTurnGroups([
      msg({ id: 'a1', role: 'assistant', content: '答一' }),
      msg({ id: 'u2', role: 'user', content: '问二' }),
      msg({ id: 'a2', role: 'assistant', content: '答二' }),
    ]);
    expect(groups.map((g) => g.assistantId)).toEqual(['a1', 'a2']);
  });
});

describe('buildTimelineEntries', () => {
  it('key/kind 序列与旧内联语义一致', () => {
    const msgs = [
      msg({ id: 'u1', role: 'user', content: '第一问' }),
      msg({ id: 'a1', role: 'assistant', content: '第一答' }),
      msg({ id: 'u2', role: 'user', content: '第二问' }),
      msg({ id: 'a2', role: 'assistant', content: '第二答' }),
      msg({ id: 's1', role: 'system', content: 'sys' }),
    ];
    const groups: TurnGroup[] = ['a1', 'a2'].map((id) => ({
      key: id,
      assistantId: id,
      reasoning: null,
      content: `${id}-c`,
      toolCalls: [],
      toolMessages: [],
      telemetry: null,
      createdAtUtc: '',
    }));
    const entries = buildTimelineEntries(msgs, groups);
    expect(entries.map((e) => `${e.key}:${e.kind}`)).toEqual(['u-u1:user', 'a1:turn', 'u-u2:user', 'a2:turn', 's-s1:system']);
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
  it('buildHistoryToc 只看每轮末条', () => {
    const entries = buildTimelineEntries(
      [
        msg({ id: 'u1', role: 'user', content: '第一问是什么' }),
        msg({ id: 'a1', role: 'assistant', content: '第一答内容' }),
        msg({ id: 'u2', role: 'user', content: '第二问是什么' }),
        msg({ id: 'a2', role: 'assistant', content: '第二答内容' }),
        msg({ id: 'a3', role: 'assistant', content: '第三答内容' }),
      ],
      ['a1', 'a2', 'a3'].map((id) => ({
        key: id,
        assistantId: id,
        reasoning: null,
        content: `${id}-c`,
        toolCalls: [],
        toolMessages: [],
        telemetry: null,
        createdAtUtc: '',
      }))
    );
    const heads = buildHistoryToc(entries, { role: 'all', roundHeadsOnly: true });
    expect(heads.every((t) => t.role !== 'system')).toBe(true);
    expect(heads.filter((t) => t.role === 'assistant')).toHaveLength(2);
  });
  it('孤立 tool 组 label 带扳手', () => {
    const toolEntries = buildTimelineEntries(
      [msg({ id: 't1', role: 'tool', content: 'out' })],
      [
        {
          key: 'tool-t1',
          assistantId: '',
          reasoning: null,
          content: null,
          toolCalls: [],
          toolMessages: [msg({ id: 't1', role: 'tool', content: 'out' })],
          telemetry: null,
          createdAtUtc: '',
        },
      ]
    );
    const toc = buildHistoryToc(toolEntries, { role: 'all', roundHeadsOnly: false });
    expect(toc[0]?.role).toBe('assistant');
    expect(String(toc[0]?.label || '').startsWith('🔧')).toBe(true);
    expect(labelOfEntry(toolEntries[0]!)).toContain('🔧');
  });
});
