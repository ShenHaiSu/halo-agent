// need02-P4：增量投影 + 轮级菜单纯函数单测（p4-proj）。
import { describe, expect, it } from 'vitest';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import {
  buildUserTurns,
  buildUserTurnsIncremental,
  createUserTurnsCache,
  turnCopyText,
  turnMessageIds,
  turnReplayAnchor,
} from './userTurn';

function user(id: string, content: string): UnifiedChatMessage {
  return { id, role: 'user', content, createdAtUtc: '2026-10-26T11:59:00Z' } as unknown as UnifiedChatMessage;
}
function assistant(id: string, content: string): UnifiedChatMessage {
  return { id, role: 'assistant', content, reasoningContent: null, createdAtUtc: '2026-10-26T11:59:10Z' } as unknown as UnifiedChatMessage;
}
function tool(id: string, callId: string, content: string): UnifiedChatMessage {
  return { id, role: 'tool', toolCallId: callId, content, createdAtUtc: '2026-10-26T11:59:20Z' } as unknown as UnifiedChatMessage;
}

describe('need02-P4 buildUserTurnsIncremental', () => {
  it('空缓存首算 = 全量结果（内容一致）', () => {
    const msgs = [user('u1', 'hi'), assistant('a1', 'hello')];
    const cache = createUserTurnsCache();
    const { entries, reused } = buildUserTurnsIncremental(msgs, {}, cache);
    expect(reused).toBe(false);
    expect(entries).toEqual(buildUserTurns(msgs, {}));
  });

  it('同指纹命中返回同一引用（模板零 diff）', () => {
    const msgs = [user('u1', 'hi'), assistant('a1', 'hello')];
    const cache = createUserTurnsCache();
    const first = buildUserTurnsIncremental(msgs, {}, cache);
    const second = buildUserTurnsIncremental(msgs, {}, cache);
    expect(second.reused).toBe(true);
    expect(second.entries).toBe(first.entries);
  });

  it('纯追加只重建末轮（前缀引用复用，结果与全量一致）', () => {
    const base = [user('u1', 'q1'), assistant('a1', 'r1'), user('u2', 'q2'), assistant('a2', 'r2')];
    const cache = createUserTurnsCache();
    const first = buildUserTurnsIncremental(base, {}, cache);
    expect(first.entries).toHaveLength(2);
    const next = [...base, assistant('a3', 'r2-2')];
    const second = buildUserTurnsIncremental(next, {}, cache);
    expect(second.reused).toBe(true);
    // 前缀轮沿用旧引用（DOM 不重建），末轮重建且内容正确。
    expect(second.entries).toHaveLength(2);
    expect(second.entries[0]).toBe(first.entries[0]);
    expect(second.entries[1]).not.toBe(first.entries[1]);
    expect(second.entries).toEqual(buildUserTurns(next, {}));
  });

  it('追加新 user 另起一轮（D3=A），旧轮引用复用', () => {
    const base = [user('u1', 'q1'), assistant('a1', 'r1'), user('u9', 'q9'), assistant('a9', 'r9')];
    const cache = createUserTurnsCache();
    const first = buildUserTurnsIncremental(base, {}, cache);
    expect(first.entries).toHaveLength(2);
    const next = [...base, user('u2', 'q2'), assistant('a2', 'r2')];
    const second = buildUserTurnsIncremental(next, {}, cache);
    expect(second.entries).toHaveLength(3);
    // 首轮沿用旧引用（DOM 不重建）；旧末轮被延续可能，需重建。
    expect(second.entries[0]).toBe(first.entries[0]);
    expect(second.entries).toEqual(buildUserTurns(next, {}));
  });

  it('非追加变更（中间改写）回退全量重建', () => {
    const base = [user('u1', 'q1'), assistant('a1', 'r1')];
    const cache = createUserTurnsCache();
    buildUserTurnsIncremental(base, {}, cache);
    const changed = [user('u1', 'q1-CHANGED'), assistant('a1', 'r1')];
    const second = buildUserTurnsIncremental(changed, {}, cache);
    expect(second.reused).toBe(false);
    expect(second.entries).toEqual(buildUserTurns(changed, {}));
  });

  it('opts 变化（isGenerating 翻转）不误复用旧运行态', () => {
    const msgs = [user('u1', 'q1'), assistant('a1', 'r1')];
    const cache = createUserTurnsCache();
    const running = buildUserTurnsIncremental(msgs, { activeAssistantId: 'a1', isGenerating: true }, cache);
    const done = buildUserTurnsIncremental([...msgs, assistant('a2', 'r2')], { activeAssistantId: 'a2', isGenerating: false }, cache);
    expect(done.entries).toEqual(buildUserTurns([...msgs, assistant('a2', 'r2')], { activeAssistantId: 'a2', isGenerating: false }));
    expect(running.entries).not.toBe(done.entries);
  });
});

describe('need02-P4 轮级菜单纯函数', () => {
  const msgs = [
    user('u1', '查一下'),
    assistant('a1', '第一段'),
    tool('t1', 'c1', 'out1'),
    tool('t2', 'c2', '[Tool Error] boom'),
    assistant('a2', '末段结论'),
  ];
  const turn = (() => {
    const entries = buildUserTurns(msgs, {});
    const e = entries[0];
    if (e.kind !== 'user-turn') throw new Error('not a user turn');
    return e.turn;
  })();

  it('turnMessageIds = user + 各 assistant + tool（阅读序）', () => {
    expect(turnMessageIds(turn)).toEqual(['u1', 'a1', 't1', 't2', 'a2']);
  });

  it('turnCopyText 含 user 全文 + 各段正文 + 工具结果', () => {
    const text = turnCopyText(turn);
    expect(text).toContain('查一下');
    expect(text).toContain('第一段');
    expect(text).toContain('末段结论');
    expect(text).toContain('out1');
    expect(text).toContain('[Tool Error] boom');
  });

  it('turnReplayAnchor 优先 user 锚（D3=A）', () => {
    expect(turnReplayAnchor(turn)).toEqual({ kind: 'user', id: 'u1' });
  });

  it('pending 轮不给 user 锚（避免重放乐观 id）', () => {
    const pend = buildUserTurns(
      [{ id: 'pending-req1', role: 'user', content: 'hi', createdAtUtc: '2026-10-26T11:59:00Z' } as unknown as UnifiedChatMessage],
      {}
    );
    const e = pend[0];
    if (e.kind !== 'user-turn') throw new Error('not a user turn');
    expect(turnReplayAnchor(e.turn)).toBeNull();
  });
});
