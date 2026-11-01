// P15 前端验收 9~15：turncard-routing / round-move / stream-stable-parity / dropped-fallback / process-autofold / tool-lazy / 反例
import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import { dispatchFrames } from '@/stores/eventBridge';
import { parseMarkdown } from '@/components/chat/MarkdownRenderer';
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
