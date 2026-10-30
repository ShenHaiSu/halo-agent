// P13 responses 契约字段回归：kind/rawType/payload/reasoningKind/responseId 透传 + 缺省回落 + dropped:boolean。
import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { normalizeToolCalls, useMessagesStore } from './messages';
import { useStreamStore } from './stream';
import { dispatchFrames } from './eventBridge';
import type { HaloFrame } from '@/bridge/types';

beforeEach(() => {
  setActivePinia(createPinia());
});

describe('normalizeToolCalls P13 字段', () => {
  it('显式读 kind/rawType/payload', () => {
    const out = normalizeToolCalls([
      { id: 't1', name: 'web_search', args: '{}', kind: 'unknown', rawType: 'web_search', payload: { a: 1 } },
    ]);
    expect(out![0]).toMatchObject({ kind: 'unknown', rawType: 'web_search', payload: { a: 1 } });
  });

  it('缺省回落 function/null/null（向后兼容旧快照）', () => {
    const out = normalizeToolCalls([{ id: 't1', name: 'read_file', args: '{}' }]);
    expect(out![0]).toMatchObject({ kind: 'function', rawType: null, payload: null });
  });

  it('非法 kind 回落 function', () => {
    const out = normalizeToolCalls([{ id: 't1', name: 'x', args: '{}', kind: 'grpc' }]);
    expect(out![0]!.kind).toBe('function');
  });
});

describe('applySnapshot P13 字段', () => {
  it('reasoningKind 缺省 full，responseId 透传', () => {
    const messages = useMessagesStore();
    messages.applySnapshot({
      messages: [
        { id: 'm1', role: 'assistant', text: 'hi', reasoning: 'r' },
        { id: 'm2', role: 'assistant', text: 'hi2', reasoningKind: 'summary', responseId: 'resp-9' },
      ],
    });
    expect(messages.items[0]!.reasoningKind).toBe('full');
    expect(messages.items[0]!.responseId).toBeUndefined();
    expect(messages.items[1]!.reasoningKind).toBe('summary');
    expect(messages.items[1]!.responseId).toBe('resp-9');
  });
});

describe('dispatchFrames P13 字段', () => {
  it('reasoning.delta kind / tool.started kind+rawType / turn.finished responseId / usage dropped:boolean', () => {
    const messages = useMessagesStore();
    const stream = useStreamStore();
    dispatchFrames([
      { t: 'evt', seq: 1, k: 'turn.started', p: { turnId: 't1' } },
      { t: 'evt', seq: 2, k: 'reasoning.delta', p: { text: 'r', kind: 'summary' } },
      {
        t: 'evt',
        seq: 3,
        k: 'tool.started',
        p: { toolCallId: 'tc1', name: 'web_search', args: '{"q":1}', kind: 'unknown', rawType: 'web_search' },
      },
      { t: 'evt', seq: 4, k: 'usage', p: { prompt: 10, completion: 5, dropped: true } },
    ] as unknown as HaloFrame[]);
    const m = messages.items[0]!;
    expect(m.reasoning).toBe('r');
    expect(m.reasoningKind).toBe('summary');
    expect(m.toolCalls![0]).toMatchObject({ kind: 'unknown', rawType: 'web_search' });
    expect(stream.activeTools.get('tc1')?.kind).toBe('unknown');
    expect(stream.usage.dropped).toBe(true);
    // turn.finished 收尾：responseId 落盘到消息（endTurn 会清空 activeTools，先断言完再收尾）。
    dispatchFrames([
      { t: 'evt', seq: 5, k: 'turn.finished', p: { turnId: 't1', text: '', truncated: false, responseId: 'resp-1' } },
    ] as unknown as HaloFrame[]);
    expect(m.responseId).toBe('resp-1');
  });
});
