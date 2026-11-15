import { describe, expect, it } from 'vitest';
import type { TimelineBlockContent } from '@shared/bridge/hostBridge';
import { buildHistoryItems, toAgentToolCard, type HistoryItem } from './historyToolCards';

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
    success: over.success ?? null,
    argsJson: over.argsJson ?? null,
    resultText: over.resultText ?? null,
    resultRef: over.resultRef ?? null,
  };
}

function toolsOf(items: HistoryItem[]) {
  return items.filter((it): it is Extract<HistoryItem, { kind: 'tool' }> => it.kind === 'tool').map((it) => it.group);
}

function kindsOf(items: HistoryItem[]) {
  return items.map((it) => it.kind);
}

function keysOf(items: HistoryItem[]) {
  return items.map((it) => it.key);
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
    const items = buildHistoryItems([call, result]);
    const tools = toolsOf(items);
    expect(tools).toHaveLength(1);
    expect(tools[0].callId).toBe('call-1');
    expect(tools[0].callBlock?.blockId).toBe('b-call-1');
    expect(tools[0].resultBlock?.blockId).toBe('b-result-1');
    expect(tools[0].functionName).toBe('read_file');
    expect(kindsOf(items)).toEqual(['tool']);
  });

  it('keeps orphan result as its own item', () => {
    const orphan = makeBlock({
      blockId: 'b-orphan',
      kind: 'tool_result',
      callId: 'missing-call',
      seq: 5,
      resultText: 'orphan-text',
    });
    const items = buildHistoryItems([orphan]);
    expect(toolsOf(items)).toHaveLength(0);
    expect(kindsOf(items)).toEqual(['tool_result']);
  });

  it('passes through blocks without callId', () => {
    const text = makeBlock({ blockId: 'b-text', kind: 'text', seq: 1, markdown: 'hello' });
    const noCallResult = makeBlock({
      blockId: 'b-nocall',
      kind: 'tool_result',
      seq: 2,
      resultText: 'x',
    });
    const items = buildHistoryItems([text, noCallResult]);
    expect(toolsOf(items)).toHaveLength(0);
    expect(kindsOf(items)).toEqual(['text', 'tool_result']);
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
    const tools = toolsOf(buildHistoryItems([call, result]));
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
    const tools = toolsOf(buildHistoryItems([call]));
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
    const tools = toolsOf(buildHistoryItems([call]));
    const errCard = toAgentToolCard(tools[0], { failed: 1, status: 'completed' });
    expect(errCard.status).toBe('error');
    expect(errCard.argsJson).toBe('{}');
    const okCard = toAgentToolCard(tools[0], { failed: 0, status: 'completed' });
    expect(okCard.status).toBe('done');
    expect(okCard.receivedBytes).toBe(okCard.argsJson.length);
  });

  it('prefers per-tool success bit over turn aggregate', () => {
    const call = makeBlock({
      blockId: 'b-call',
      kind: 'tool_call',
      callId: 'c-bit',
      functionName: 'exec_command',
      seq: 1,
      argsJson: '{}',
    });
    const okResult = makeBlock({
      blockId: 'b-res-ok',
      kind: 'tool_result',
      callId: 'c-bit',
      seq: 2,
      resultText: 'ok',
      success: true,
    });
    const failResult = makeBlock({
      blockId: 'b-res-fail',
      kind: 'tool_result',
      callId: 'c-bit',
      seq: 2,
      resultText: 'ok',
      success: false,
    });
    const okGroup = toolsOf(buildHistoryItems([call, okResult]))[0];
    const failGroup = toolsOf(buildHistoryItems([call, failResult]))[0];
    expect(toAgentToolCard(okGroup, { failed: 1, status: 'completed' }).status).toBe('done');
    expect(toAgentToolCard(failGroup, { failed: 0, status: 'completed' }).status).toBe('error');
  });

  it('falls back to error-text heuristic when success bit is unknown', () => {
    const call = makeBlock({
      blockId: 'b-call',
      kind: 'tool_call',
      callId: 'c-old',
      functionName: 'read_file',
      seq: 1,
      argsJson: '{}',
    });
    const errorTextResult = makeBlock({
      blockId: 'b-res-err',
      kind: 'tool_result',
      callId: 'c-old',
      seq: 2,
      resultText: '[Tool Error - read_file]: boom',
      success: null,
    });
    const plainResult = makeBlock({
      blockId: 'b-res-plain',
      kind: 'tool_result',
      callId: 'c-old',
      seq: 2,
      resultText: 'plain output',
    });
    const errGroup = toolsOf(buildHistoryItems([call, errorTextResult]))[0];
    const plainGroup = toolsOf(buildHistoryItems([call, plainResult]))[0];
    expect(toAgentToolCard(errGroup, { failed: 0, status: 'completed' }).status).toBe('error');
    expect(toAgentToolCard(plainGroup, { failed: 1, status: 'completed' }).status).toBe('error');
    expect(toAgentToolCard(plainGroup, { failed: 0, status: 'completed' }).status).toBe('done');
  });
});

describe('buildHistoryItems 保序', () => {
  function interleaved() {
    return [
      makeBlock({ blockId: 'b-rsn', kind: 'reasoning', seq: 1, markdown: '思考' }),
      makeBlock({ blockId: 'b-txt', kind: 'text', seq: 2, markdown: '过程正文' }),
      makeBlock({
        blockId: 'b-call-1',
        kind: 'tool_call',
        callId: 'c1',
        functionName: 'read_file',
        seq: 3,
        argsJson: '{}',
      }),
      makeBlock({
        blockId: 'b-res-1',
        kind: 'tool_result',
        callId: 'c1',
        functionName: 'read_file',
        seq: 4,
        resultText: 'ok',
      }),
      makeBlock({ blockId: 'b-txt-2', kind: 'text', seq: 5, markdown: '收尾' }),
      makeBlock({
        blockId: 'b-call-2',
        kind: 'tool_call',
        callId: 'c2',
        functionName: 'exec_command',
        seq: 6,
        argsJson: '{}',
      }),
    ];
  }

  it('交错输入保持原序，已配对结果不占位', () => {
    const items = buildHistoryItems(interleaved());
    expect(kindsOf(items)).toEqual(['reasoning', 'text', 'tool', 'text', 'tool']);
  });

  it('输入置换后输出随之置换', () => {
    const blocks = interleaved();
    const reversed = [...blocks].reverse();
    // 反序输入中结果块先于调用块出现，无法被卡收编，按原位输出
    const items = buildHistoryItems(reversed);
    expect(kindsOf(items)).toEqual(['tool', 'text', 'tool_result', 'tool', 'text', 'reasoning']);
    const forward = buildHistoryItems(blocks);
    expect(kindsOf(forward)).toEqual(['reasoning', 'text', 'tool', 'text', 'tool']);
  });

  it('工具卡位置等于调用块位置', () => {
    const blocks = interleaved();
    const items = buildHistoryItems(blocks);
    const toolIndex = items.findIndex((it) => it.kind === 'tool');
    // 调用块是输入第 3 个（下标 2），之前无被收编项，故输出下标同样为 2
    expect(toolIndex).toBe(2);
    expect(blocks[2].blockId).toBe('b-call-1');
  });

  it('重复结果块只收录首个且不另起项', () => {
    const call = makeBlock({
      blockId: 'b-call',
      kind: 'tool_call',
      callId: 'c-dup-res',
      functionName: 'read_file',
      seq: 1,
      argsJson: '{}',
    });
    const first = makeBlock({
      blockId: 'b-res-first',
      kind: 'tool_result',
      callId: 'c-dup-res',
      seq: 2,
      resultText: 'first',
    });
    const second = makeBlock({
      blockId: 'b-res-second',
      kind: 'tool_result',
      callId: 'c-dup-res',
      seq: 3,
      resultText: 'second',
    });
    const items = buildHistoryItems([call, first, second]);
    expect(kindsOf(items)).toEqual(['tool']);
    expect(toolsOf(items)[0].resultBlock?.blockId).toBe('b-res-first');
  });

  it('重复调用块仍只出一项且保留首块', () => {
    const first = makeBlock({
      blockId: 'b-call-first',
      kind: 'tool_call',
      callId: 'c-dup-call',
      functionName: 'read_file',
      seq: 1,
      argsJson: '{"a":1}',
    });
    const second = makeBlock({
      blockId: 'b-call-second',
      kind: 'tool_call',
      callId: 'c-dup-call',
      functionName: 'read_file',
      seq: 2,
      argsJson: '{"a":2}',
    });
    const items = buildHistoryItems([first, second]);
    expect(kindsOf(items)).toEqual(['tool']);
    const tools = toolsOf(items);
    expect(tools).toHaveLength(1);
    expect(tools[0].callBlock?.blockId).toBe('b-call-first');
  });

  it('全孤儿结果按原位输出', () => {
    const text = makeBlock({ blockId: 'b-final', kind: 'text', seq: 1, markdown: '结论' });
    const resA = makeBlock({
      blockId: 'b-res-a',
      kind: 'tool_result',
      callId: 'missing-a',
      seq: 2,
      resultText: 'a',
    });
    const resB = makeBlock({
      blockId: 'b-res-b',
      kind: 'tool_result',
      callId: 'missing-b',
      seq: 3,
      resultText: 'b',
    });
    expect(kindsOf(buildHistoryItems([text, resA, resB]))).toEqual(['text', 'tool_result', 'tool_result']);
  });

  it('重复调用结果稳定', () => {
    const blocks = interleaved();
    expect(keysOf(buildHistoryItems(blocks))).toEqual(keysOf(buildHistoryItems(blocks)));
  });

  it('空输入与空值防御返回空数组', () => {
    expect(buildHistoryItems([])).toEqual([]);
    expect(buildHistoryItems(null as unknown as TimelineBlockContent[])).toEqual([]);
  });
});
