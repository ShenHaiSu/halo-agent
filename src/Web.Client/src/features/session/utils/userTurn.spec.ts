// need02-P2：用户轮次聚合单测（10 组，锁死 04-P2 §关键语义 + 05 红线）。
import { describe, expect, it } from 'vitest';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import {
  buildUserTurns,
  clearPendingAlias,
  earlierSteps,
  fullSummaryOf,
  isSingleFlatTurn,
  lastStepContent,
  rememberPendingAlias,
  summaryOf,
} from './userTurn';

const NOW = Date.parse('2026-10-26T12:00:00Z');

function user(id: string, content: string, createdAtUtc = '2026-10-26T11:59:00Z'): UnifiedChatMessage {
  return { id, role: 'user', content, createdAtUtc } as unknown as UnifiedChatMessage;
}
function assistant(id: string, content: string, opts: { reasoning?: string; tools?: Array<{ id: string; name: string }>; createdAtUtc?: string } = {}): UnifiedChatMessage {
  return {
    id,
    role: 'assistant',
    content,
    reasoningContent: opts.reasoning ?? null,
    toolCalls: (opts.tools || []).map((t) => ({ id: t.id, type: 'function', function: { name: t.name, arguments: '{}' } })),
    createdAtUtc: opts.createdAtUtc || '2026-10-26T11:59:10Z',
  } as unknown as UnifiedChatMessage;
}
function tool(id: string, callId: string, content: string): UnifiedChatMessage {
  return { id, role: 'tool', toolCallId: callId, content, createdAtUtc: '2026-10-26T11:59:20Z' } as unknown as UnifiedChatMessage;
}
function system(id: string, content: string): UnifiedChatMessage {
  return { id, role: 'system', content, createdAtUtc: '2026-10-26T11:59:30Z' } as unknown as UnifiedChatMessage;
}

describe('need02-P2 buildUserTurns', () => {
  it('1. 单步无工具轮不折叠（user + 正文直出）', () => {
    const entries = buildUserTurns([user('u1', '你好'), assistant('a1', '世界')], { now: NOW });
    expect(entries).toHaveLength(1);
    const e = entries[0];
    expect(e.kind).toBe('user-turn');
    if (e.kind !== 'user-turn') return;
    expect(e.turn.stepCount).toBe(1);
    expect(e.turn.toolCount).toBe(0);
    expect(isSingleFlatTurn(e.turn)).toBe(true);
    expect(lastStepContent(e.turn)).toBe('世界');
  });

  it('2. 多步折叠（3 iteration 混合：2 正文 + 5 工具 + 失败 1）', () => {
    const msgs = [
      user('u1', '查一下'),
      assistant('a1', '第一段', { tools: [{ id: 'c1', name: 'list' }, { id: 'c2', name: 'read' }] }),
      tool('t1', 'c1', 'out1'),
      tool('t2', 'c2', '[Tool Error] boom'),
      assistant('a2', '', { reasoning: '思考中' }),
      tool('t3', 'c3', 'out3'),
      tool('t4', 'c4', 'out4'),
      tool('t5', 'c5', 'out5'),
      assistant('a3', '末段结论'),
    ];
    const entries = buildUserTurns(msgs, { now: NOW });
    expect(entries).toHaveLength(1);
    const t = entries[0].kind === 'user-turn' ? entries[0].turn : null;
    expect(t).not.toBeNull();
    expect(t!.stepCount).toBe(3);
    expect(t!.toolCount).toBe(5);
    expect(t!.failedCount).toBe(1);
    expect(isSingleFlatTurn(t!)).toBe(false);
    expect(lastStepContent(t!)).toBe('末段结论');
    // 展开区不再重复末段
    expect(earlierSteps(t!).every((s) => s.content !== '末段结论')).toBe(true);
    // 折叠行含过程信息（B 模式无字数）
    expect(t!.summary).toContain('工具5');
    expect(t!.summary).toContain('失败1');
    expect(t!.summary).not.toContain('正文');
  });

  it('3. 失败计数口径与 failedToolCount 同源', () => {
    const entries = buildUserTurns([user('u1', 'x'), assistant('a1', 'y'), tool('t1', 'c1', 'ok'), tool('t2', 'c2', "xx [Tool Error: VIEW_IMAGE] yy")], { now: NOW });
    const t = entries[0].kind === 'user-turn' ? entries[0].turn : null;
    expect(t!.failedCount).toBe(1);
    expect(t!.phase).toBe('failed');
  });

  it('4. 字数口径 UTF-16（emoji 计 2），0 字隐藏', () => {
    // 'A😀' = 1 + 2 = 3
    const entries = buildUserTurns([user('u1', 'x'), assistant('a1', 'A😀')], { now: NOW });
    const t = entries[0].kind === 'user-turn' ? entries[0].turn : null;
    expect(t!.contentChars).toBe('A😀'.length);
    expect(t!.contentChars).toBe(3);
    // 0 字隐藏：summary/fullSummary 均不出现“正文0字”
    const empty = buildUserTurns([user('u2', 'x'), assistant('a2', '')], { now: NOW });
    const t2 = empty[0].kind === 'user-turn' ? empty[0].turn : null;
    expect(t2!.contentChars).toBe(0);
    expect(t2!.summary).not.toContain('正文');
    expect(t2!.fullSummary).not.toContain('正文0字');
    expect(fullSummaryOf({ duration: '', toolCount: 0, failedCount: 0, contentChars: 5, reasoningChars: 0 })).toContain('正文5字');
    expect(summaryOf({ duration: '耗时12s', toolCount: 5, failedCount: 1 })).toBe('耗时12s · 工具5 · 失败1');
  });

  it('5. key 稳定：uturn-<首userId>，追加不变化', () => {
    const m1 = [user('u1', 'x'), assistant('a1', 'y')];
    const e1 = buildUserTurns(m1, { now: NOW });
    const k1 = e1[0].key;
    expect(k1).toBe('uturn-u1');
    const e2 = buildUserTurns([...m1, assistant('a2', 'z')], { now: NOW });
    expect(e2[0].key).toBe(k1);
  });

  it('6. 孤儿轮：无 user 开局残留不丢失', () => {
    const entries = buildUserTurns([assistant('a1', '旧正文'), tool('t1', 'c1', 'out')], { now: NOW });
    expect(entries).toHaveLength(1);
    const t = entries[0].kind === 'user-turn' ? entries[0].turn : null;
    expect(t!.userMsg).toBeNull();
    expect(t!.stepCount).toBe(1);
    expect(t!.toolCount).toBe(1);
  });

  it('7. pending 轮：乐观 user 独立成轮，转正后别名保持 key', () => {
    clearPendingAlias();
    const pend = user('pending-req1', '发送中');
    const e1 = buildUserTurns([pend], { now: NOW });
    const k1 = e1[0].key;
    expect(k1).toBe('uturn-pending-req1');
    const t1 = e1[0].kind === 'user-turn' ? e1[0].turn : null;
    expect(t1!.pending).toBe(true);
    // 对账命中后记住别名，再以真实 id 聚合 key 仍为 pending key
    rememberPendingAlias('u-real-1', k1);
    const e2 = buildUserTurns([user('u-real-1', '发送中')], { now: NOW });
    expect(e2[0].key).toBe(k1);
    clearPendingAlias();
  });

  it('8. system 不断轮（阅读锚点独立 entry）', () => {
    const entries = buildUserTurns([user('u1', 'x'), assistant('a1', 'y'), system('s1', '摘要'), user('u2', 'z'), assistant('a2', 'w')], { now: NOW });
    const turns = entries.filter((e) => e.kind === 'user-turn');
    const systems = entries.filter((e) => e.kind === 'system');
    expect(turns).toHaveLength(2);
    expect(systems).toHaveLength(1);
    // 时序：turn1, system, turn2
    expect(entries.map((e) => e.kind)).toEqual(['user-turn', 'system', 'user-turn']);
  });

  it('9. 重试另起一轮（新 user 锚，D3=A）', () => {
    const entries = buildUserTurns([user('u1', 'x'), assistant('a1', 'y'), user('u2', 'x'), assistant('a2', 'y2')], { now: NOW });
    expect(entries.filter((e) => e.kind === 'user-turn')).toHaveLength(2);
    expect(entries[0].key).toBe('uturn-u1');
    expect(entries[1].key).toBe('uturn-u2');
  });

  it('10. 空轮与纯 user 轮不炸', () => {
    expect(buildUserTurns([], { now: NOW })).toEqual([]);
    expect(buildUserTurns(null, { now: NOW })).toEqual([]);
    const onlyUser = buildUserTurns([user('u1', 'hi')], { now: NOW });
    expect(onlyUser).toHaveLength(1);
    const t = onlyUser[0].kind === 'user-turn' ? onlyUser[0].turn : null;
    expect(t!.stepCount).toBe(0);
    expect(isSingleFlatTurn(t!)).toBe(true);
  });
});
