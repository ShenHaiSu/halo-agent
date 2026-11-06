import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { buildTitle, EMPTY_TITLE, isPlaceholderTitle } from '@/utils/sessionTitle';
import { useSessionsStore } from './sessions';
import { useStreamStore } from './stream';
import { useMessagesStore } from './messages';
import { dispatchFrames } from './eventBridge';

describe('sessionTitle buildTitle', () => {
  it('空输入返回空标题', () => {
    expect(buildTitle(null)).toBe(EMPTY_TITLE);
    expect(buildTitle('')).toBe(EMPTY_TITLE);
    expect(buildTitle('  \n ')).toBe(EMPTY_TITLE);
  });

  it('裁剪换行空格化', () => {
    expect(buildTitle('  hi\nthere ')).toBe('hi there');
    // \r 保持现状（只换 \n），与后端逐行对齐。
    expect(buildTitle('a\r\nb')).toBe('a\r b');
  });

  it('60 字以内原样，61 字截断加省略号', () => {
    const sixty = 'a'.repeat(60);
    expect(buildTitle(sixty)).toBe(sixty);
    expect(buildTitle('a'.repeat(61))).toBe('a'.repeat(60) + '…');
  });

  it('CJK 按 UTF-16 码元计', () => {
    expect(buildTitle('中'.repeat(61))).toBe('中'.repeat(60) + '…');
  });
});

describe('sessionTitle isPlaceholderTitle', () => {
  it('占位标题可覆盖', () => {
    expect(isPlaceholderTitle('新会话')).toBe(true);
    expect(isPlaceholderTitle('新对话')).toBe(true);
    expect(isPlaceholderTitle('')).toBe(true);
    expect(isPlaceholderTitle(EMPTY_TITLE)).toBe(true);
  });

  it('真实标题不可覆盖', () => {
    expect(isPlaceholderTitle('你好，今天天气怎么样')).toBe(false);
    expect(isPlaceholderTitle('我的备注')).toBe(false);
  });
});

describe('applySessionUpdate 合并', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.restoreAllMocks();
  });

  it('合并标题与计数', () => {
    const sessions = useSessionsStore();
    sessions.items = [{ id: 's1', title: '新会话', note: null, mtimeMs: 1 } as never];
    sessions.applySessionUpdate({ id: 's1', title: 'hello world', mtimeMs: 123, userMsgs: 1, assistantMsgs: 1 });
    expect(sessions.items[0]!.title).toBe('hello world');
    expect(sessions.items[0]!.mtimeMs).toBe(123);
  });

  it('不覆盖 note，但后台更新 title', () => {
    const sessions = useSessionsStore();
    sessions.items = [{ id: 's1', title: '旧标题', note: '我的备注', mtimeMs: 1 } as never];
    sessions.applySessionUpdate({ id: 's1', title: '新标题', mtimeMs: 2 });
    expect(sessions.items[0]!.note).toBe('我的备注');
    expect(sessions.items[0]!.title).toBe('新标题');
    expect(sessions.list[0]!.title).toBe('我的备注');
  });

  it('空标题不刷空', () => {
    const sessions = useSessionsStore();
    sessions.items = [{ id: 's1', title: '真标题', note: null, mtimeMs: 1 } as never];
    sessions.applySessionUpdate({ id: 's1', title: '' });
    expect(sessions.items[0]!.title).toBe('真标题');
  });

  it('未知 id 忽略', () => {
    const sessions = useSessionsStore();
    sessions.items = [{ id: 's1', title: '真标题', note: null, mtimeMs: 1 } as never];
    sessions.applySessionUpdate({ id: 'ghost', title: 'x' });
    expect(sessions.items).toHaveLength(1);
  });

  it('turn.finished 捎带即时合并', () => {
    const sessions = useSessionsStore();
    const stream = useStreamStore();
    const messages = useMessagesStore();
    sessions.items = [{ id: 's1', title: '新会话', note: null, mtimeMs: 1 } as never];
    sessions.currentId = 's1';
    stream.beginTurn('t1');
    messages.ensureTurn('t1');
    dispatchFrames([
      {
        t: 'evt',
        seq: 1,
        k: 'turn.finished',
        p: { turnId: 't1', text: 'done', finalUsed: 1, session: { id: 's1', title: '首问标题', mtimeMs: 7 } },
      } as never,
    ]);
    expect(sessions.items[0]!.title).toBe('首问标题');
    expect(stream.busy).toBe(false);
  });
});
