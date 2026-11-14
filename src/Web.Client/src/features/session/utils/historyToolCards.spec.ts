import { describe, expect, it } from 'vitest';
import type { TimelineBlockContent } from '@shared/bridge/hostBridge';
import { pairToolGroupsInOrder, toAgentToolCard } from './historyToolCards';

function makeBlock(over: Partial<TimelineBlockContent> & { blockId: string }): TimelineBlockContent {
  return {
    blockId: over.blockId,
    callId: over.callId ?? null,
    functionName: over.functionName ?? null,
    seq: over.seq ?? 0,
    kind: over.kind ?? 'text',
    tag: over.tag ?? 'process',
    markdown: over.markdown ?? '',
    preview: over.preview ?? '',
    truncated: over.truncated ?? false,
    argsJson: over.argsJson ?? null,
    resultText: over.resultText ?? null,
    resultRef: over.resultRef ?? null,
  };
}

describe('historyToolCards pairing', () => {
  it('pairs one call with one result', () => {
    const call = makeBlock({
      blockId: 'b-call-1',
      kind: 'tool_call',
      callId: 'call-1',
      functionName: 'read_file',
      seq: 1,
      argsJson: '{"path":"a.txt"}',
    });
    const result = makeBlock({
      blockId: 'b-result-1',
      kind: 'tool_result',
      callId: 'call-1',
      functionName: 'read_file',
      seq: 2,
      resultText: 'hello',
    });
    const { tools, others } = pairToolGroupsInOrder([call, result]);
    expect(tools).toHaveLength(1);
    expect(tools[0].callId).toBe('call-1');
    expect(tools[0].callBlock?.blockId).toBe('b-call-1');
    expect(tools[0].resultBlock?.blockId).toBe('b-result-1');
    expect(tools[0].functionName).toBe('read_file');
    expect(others).toHaveLength(0);
  });

  it('keeps orphan result in others', () => {
    const orphan = makeBlock({
      blockId: 'b-orphan',
      kind: 'tool_result',
      callId: 'missing-call',
      seq: 5,
      resultText: 'orphan-text',
    });
    const { tools, others } = pairToolGroupsInOrder([orphan]);
    expect(tools).toHaveLength(0);
    expect(others.map((b) => b.blockId)).toContain('b-orphan');
  });

  it('keeps multiple groups in seq order', () => {
    const callB = makeBlock({
      blockId: 'b-call-b',
      kind: 'tool_call',
      callId: 'call-b',
      functionName: 'list_dir',
      seq: 20,
      argsJson: '{}',
    });
    const callA = makeBlock({
      blockId: 'b-call-a',
      kind: 'tool_call',
      callId: 'call-a',
      functionName: 'exec_command',
      seq: 10,
      argsJson: '{}',
    });
    const resultA = makeBlock({
      blockId: 'b-result-a',
      kind: 'tool_result',
      callId: 'call-a',
      seq: 11,
      resultText: 'ok',
    });
    const { tools } = pairToolGroupsInOrder([callB, callA, resultA]);
    expect(tools.map((g) => g.callId)).toEqual(['call-a', 'call-b']);
  });

  it('passes through blocks without callId', () => {
    const text = makeBlock({ blockId: 'b-text', kind: 'text', seq: 1, markdown: 'hello' });
    const noCallResult = makeBlock({
      blockId: 'b-nocall',
      kind: 'tool_result',
      seq: 2,
      resultText: 'x',
    });
    const { tools, others } = pairToolGroupsInOrder([text, noCallResult]);
    expect(tools).toHaveLength(0);
    expect(others).toHaveLength(2);
  });

  it('prefers call side function name', () => {
    const call = makeBlock({
      blockId: 'b-call',
      kind: 'tool_call',
      callId: 'c1',
      functionName: 'exec_command',
      seq: 1,
      argsJson: '{}',
    });
    const result = makeBlock({
      blockId: 'b-res',
      kind: 'tool_result',
      callId: 'c1',
      functionName: 'read_file',
      seq: 2,
      resultText: 'out',
    });
    const { tools } = pairToolGroupsInOrder([call, result]);
    expect(tools[0].functionName).toBe('exec_command');
  });

  it('falls back to tool when function name is missing', () => {
    const call = makeBlock({
      blockId: 'b-call',
      kind: 'tool_call',
      callId: 'c2',
      seq: 1,
      argsJson: '{}',
    });
    const { tools } = pairToolGroupsInOrder([call]);
    expect(tools[0].functionName).toBe('tool');
  });

  it('maps failed turn to error status and defaults args', () => {
    const call = makeBlock({
      blockId: 'b-call',
      kind: 'tool_call',
      callId: 'c3',
      functionName: 'grep_search',
      seq: 1,
      argsJson: '',
    });
    const { tools } = pairToolGroupsInOrder([call]);
    const errCard = toAgentToolCard(tools[0], { failed: 1, status: 'completed' });
    expect(errCard.status).toBe('error');
    expect(errCard.argsJson).toBe('{}');
    const okCard = toAgentToolCard(tools[0], { failed: 0, status: 'completed' });
    expect(okCard.status).toBe('done');
    expect(okCard.receivedBytes).toBe(okCard.argsJson.length);
  });
});
