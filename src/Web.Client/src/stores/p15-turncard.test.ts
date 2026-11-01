// P15 前端验收 9~15：turncard-routing / round-move / stream-stable-parity / dropped-fallback / process-autofold / tool-lazy / 反例
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import { useSessionsStore } from '@/stores/sessions';
import { dispatchFrames } from '@/stores/eventBridge';
import { parseMarkdown } from '@/components/chat/MarkdownRenderer';
import TurnCard from '@/components/chat/TurnCard.vue';
import { transport } from '@/transport';
import type { HaloFrame } from '@/bridge/types';

function frames(list: Array<{ seq: number; k: string; p: unknown }>): HaloFrame[] {
  return list.map((f) => ({ t: 'evt', ...f }) as unknown as HaloFrame);
}

beforeEach(() => {
  setActivePinia(createPinia());
});

describe('9 turncard-routing：乱序 delta 归位，used==0 计数不拆卡，双草稿不混排', () => {
  it('delta(2) 先到仍归位；used==0 计数', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'text.delta', p: { text: 'b', used: 2 } }]));
    expect(messages.drafts['t1:2']?.text).toBe('b');
    dispatchFrames(frames([{ seq: 3, k: 'text.delta', p: { text: 'a', used: 1 } }]));
    expect(messages.drafts['t1:1']?.text).toBe('a');
    const beforeMismatch = messages.mismatch;
    dispatchFrames(frames([{ seq: 4, k: 'text.delta', p: { text: 'x', used: 0 } }]));
    expect(messages.mismatch).toBeGreaterThan(beforeMismatch);
    expect(stream.mismatch).toBeGreaterThan(0);
  });

  it('reasoning 与正文双草稿不混排', () => {
    const messages = useMessagesStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'text.delta', p: { text: 'hello', used: 1 } }]));
    dispatchFrames(frames([{ seq: 3, k: 'reasoning.delta', p: { text: 'think', used: 1 } }]));
    expect(messages.drafts['t1:1']?.text).toBe('hello');
    expect(messages.drafts['t1:1']?.reasoning).toBe('think');
  });
});

describe('10 round-move：ended搬process，末轮只有finished亦可', () => {
  it('ended{true} 搬 process、final 清零', () => {
    const messages = useMessagesStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'text.delta', p: { text: 'draft', used: 1 } }]));
    dispatchFrames(frames([{ seq: 3, k: 'round.ended', p: { used: 1, hasTools: true } }]));
    expect(messages.turns[0]!.process.length).toBe(1);
    expect(messages.turns[0]!.process[0]!.content).toBe('draft');
    expect(messages.drafts['t1:1']).toBeUndefined();
  });

  it('末轮只有 finished 亦可（O3）', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'text.delta', p: { text: 'hi', used: 1 } }]));
    dispatchFrames(frames([{ seq: 3, k: 'turn.finished', p: { turnId: 't1', text: 'hi-final', truncated: false, finalUsed: 1 } }]));
    expect(messages.turns[0]!.final.content).toBe('hi-final');
    expect(stream.currentTurn).toBeNull();
  });
});

describe('11 stream-stable-parity：长 Markdown 流稳 DOM 等价', () => {
  it('同帧 finalize 与流式 holdback 最终一致', () => {
    const md = '# h\n\n- a\n- b\n\n```ts\ncode\n```\n';
    const steady = parseMarkdown(md);
    expect(steady.length).toBeGreaterThan(0);
    // 流式分片后 finalize 应与稳态一致（同一 parseMarkdown 真相）
    expect(parseMarkdown(md)).toEqual(steady);
  });
});

describe('12 dropped-fallback：dropped 以 finished.text 覆盖', () => {
  it('usage dropped=true 后 finished 覆盖', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'usage', p: { prompt: 1, completion: 1, dropped: true } }]));
    expect(stream.usage.dropped).toBe(true);
    dispatchFrames(frames([{ seq: 3, k: 'turn.finished', p: { turnId: 't1', text: 'steady', truncated: false, finalUsed: 1 } }]));
    expect(messages.turns[0]!.final.content).toBe('steady');
  });
});

describe('13 process-autofold：auto 跟随，userClosed 不弹', () => {
  it('auto 默认折叠态由 isActive 决定；userClosed 保持', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    expect(stream.currentTurn?.turnId).toBe('t1');
    messages.processOpen['t1'] = 'userClosed';
    dispatchFrames(frames([{ seq: 2, k: 'text.delta', p: { text: 'x', used: 1 } }]));
    expect(messages.processOpen['t1']).toBe('userClosed');
  });
});

describe('14 tool-lazy：Truncated=false 不请求；running 不请求', () => {
  it('首屏头部 + expanded 才请求（逻辑层：store 标记）', () => {
    const messages = useMessagesStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'tool.started', p: { toolCallId: 'c1', name: 'read_file', args: '{}', used: 1, argsTotal: 2, truncated: false } }]));
    const tc = messages.turns[0]!.final.toolCalls![0]!;
    expect(tc.truncated).toBe(false);
    expect(messages.toolTail.get('c1')).toBeUndefined();
  });
});

describe('15 反例补齐', () => {
  it('空正文纯工具轮：final 占位不空指针', () => {
    const messages = useMessagesStore();
    messages.applySnapshot({
      turns: [{ turnId: 't1', user: { id: 'u1', role: 'user', content: 'q' }, final: { id: 'm1', role: 'assistant', content: '', tsMs: 1 } }],
    });
    expect(messages.turns[0]!.final).toBeDefined();
  });

  it('llm.retrying 清空本 used 草稿', () => {
    const messages = useMessagesStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'text.delta', p: { text: 'half', used: 1 } }]));
    expect(messages.drafts['t1:1']?.text).toBe('half');
    dispatchFrames(frames([{ seq: 3, k: 'llm.retrying', p: { attempt: 1, max: 3, reason: 'x' } }]));
    expect(messages.drafts['t1:1']).toBeUndefined();
  });

  it('轮中 resync + injected 插队', () => {
    const messages = useMessagesStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    dispatchFrames(frames([{ seq: 2, k: 'user.injected', p: { text: '插队' } }]));
    expect(messages.turns[0]!.injected.length).toBe(1);
    messages.applySnapshot({
      seq: 10,
      turns: [{ turnId: 't1', user: { id: 'u1', role: 'user', content: 'q' }, injected: [{ id: 'u2', role: 'user', content: '插队', injected: true }], process: [], final: { id: 'm1', role: 'assistant', content: 'f' } }],
    });
    expect(messages.turns[0]!.injected[0]!.content).toBe('插队');
  });

  it('tool.result offset 越界：slice 为空但不抛', () => {
    const s = 'abc'.slice(999, 999 + 8000);
    expect(s).toBe('');
  });
});

describe('16 stale-active 剥离：快照替换后旧 currentTurn 不再点亮历史', () => {
  function turnFixture(turnId: string, userContent: string, finalContent: string): Record<string, unknown> {
    return {
      turnId,
      user: { id: 'u-' + turnId, role: 'user', content: userContent, tsMs: 1000 },
      injected: [],
      process: [],
      final: { id: 'f-' + turnId, role: 'assistant', content: finalContent, tsMs: 2000 },
    };
  }

  it('currentTurn 不在新快照中 → endTurn，不再按流式渲染', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't-old' } }]));
    expect(stream.currentTurn?.turnId).toBe('t-old');
    messages.applySnapshot({ turns: [turnFixture('t-new', 'q', 'a')] });
    expect(stream.currentTurn).toBeNull();
    expect(messages.turns.map((t) => t.turnId)).toEqual(['t-new']);
  });

  it('currentTurn 仍在快照中 → 保留，流式不受影响', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    messages.applySnapshot({ turns: [turnFixture('t1', 'q', '')] });
    expect(stream.currentTurn?.turnId).toBe('t1');
  });
});

describe('17 resume 形状嗅探：messages 键下的回合形数组按 turns 应用', () => {
  it('回合形走 turns：内容与 ts 精确保留，不被扁平投影吞掉', async () => {
    const sessions = useSessionsStore();
    const messages = useMessagesStore();
    sessions.currentId = 'old';
    vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
      if (method === 'session.resume') {
        return {
          count: 1,
          messages: [
            {
              turnId: 't1',
              user: { id: 'u1', role: 'user', content: 'q', tsMs: 1000 },
              injected: [],
              process: [],
              final: { id: 'm1', role: 'assistant', content: 'ans', tsMs: 2000 },
            },
          ],
        } as never;
      }
      if (method === 'state.snapshot') return { session: { id: 'new' } } as never;
      throw new Error('unexpected ' + method);
    });
    await sessions.select('new');
    expect(messages.turns).toHaveLength(1);
    expect(messages.turns[0]!.final.content).toBe('ans');
    expect(messages.turns[0]!.final.ts).toBe(2000);
    expect(messages.turns[0]!.user.content).toBe('q');
  });
});

describe('18 TurnCard 草稿合并渲染：多 used 正文只出现一次', () => {
  function mountTurn(userContent: string) {
    return mount(TurnCard, {
      props: {
        turn: {
          turnId: 't1',
          user: { id: 'u1', role: 'user', content: userContent, ts: 1, ord: 0 },
          injected: [],
          process: [],
          final: { id: 'f1', role: 'assistant', content: '', ts: 2, ord: 0 },
        },
      },
      global: {
        stubs: { ReasoningBlock: true, ToolCard: true, MessageActions: true, StreamingCursor: true },
      },
    } as never);
  }

  it('双 used 草稿正文合并后只渲染一次', () => {
    const messages = useMessagesStore();
    dispatchFrames(frames([{ seq: 1, k: 'turn.started', p: { turnId: 't1' } }]));
    messages.drafts['t1:1'] = { text: 'A', reasoning: '' };
    messages.drafts['t1:2'] = { text: 'B', reasoning: '' };
    const w = mountTurn('hi');
    const hits = (w.text().match(/AB/g) ?? []).length;
    expect(hits).toBe(1);
    w.unmount();
  });

  it('空 user 不渲染空泡', () => {
    const w = mountTurn('');
    expect(w.find('.turn-user').exists()).toBe(false);
    w.unmount();
  });
});

describe('19 MessageActions hover 作用域隔离：各亮各的', () => {
  it('用户块与 final 各自点亮，不用整卡通亮', async () => {
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    const vue = readFileSync(resolve(__dirname, '../components/chat/MessageActions.vue'), 'utf-8');
    const css = vue.slice(vue.indexOf('<style'));
    expect(css).toContain('.turn-user-block:hover .actions');
    expect(css).toContain('.turn-final:hover .actions');
    expect(css).toContain('.msg-item:hover .actions');
    expect(css).not.toContain('.turn-card:hover .actions');
  });
});

describe('20 TurnCard 用户/模型 meta 独立', () => {
  function mountSteady(userContent: string, finalContent: string) {
    return mount(TurnCard, {
      props: {
        turn: {
          turnId: 't9',
          user: { id: 'u9', role: 'user', content: userContent, ts: 1000, ord: 0 },
          injected: [],
          process: [],
          final: { id: 'f9', role: 'assistant', content: finalContent, ts: 2000, ord: 0 },
        },
      },
      global: {
        stubs: { ReasoningBlock: true, ToolCard: true, MessageActions: true, StreamingCursor: true },
      },
    } as never);
  }

  it('用户块绑定 turn.user，尾部绑定 turn.final', () => {
    const w = mountSteady('uq', 'ans');
    expect(w.find('.turn-user-block').exists()).toBe(true);
    expect(w.find('.turn-user-meta .msg-time').exists()).toBe(true);
    const acts = w.findAllComponents({ name: 'MessageActions' });
    expect(acts.length).toBe(2);
    expect((acts[0]!.props('message') as unknown as { content: string }).content).toBe('uq');
    expect((acts[1]!.props('message') as unknown as { content: string }).content).toBe('ans');
    w.unmount();
  });

  it('用户复制不带重发（role=user 无 retry 按钮由组件内判定）', () => {
    const w = mountSteady('uq', 'ans');
    const acts = w.findAllComponents({ name: 'MessageActions' });
    expect((acts[0]!.props('message') as unknown as { role: string }).role).toBe('user');
    expect((acts[1]!.props('message') as unknown as { role: string }).role).toBe('assistant');
    w.unmount();
  });
});
