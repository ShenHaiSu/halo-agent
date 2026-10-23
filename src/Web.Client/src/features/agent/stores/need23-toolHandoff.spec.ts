/**
 * need23-R7：工具生命周期在「活动容器 ↔ 稳态折叠栏」之间的交接单测。
 *
 * 背景（用户实测 bug）：工具调用完毕后整轮"收起来"，稳态不挂工具信息；
 * 下一轮又闪现一下再收起来。根因是去重集合用了整轮累计的 toolCards，
 * 把已落盘的工具永久拉黑。本测试直接断言两侧渲染数据，补上原测试盲区。
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { haloEventBus } from '@shared/bridge/hostBridge';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentCards } from '@features/agent/stores/useAgentCards';
import { useAgentTelemetry } from '@features/agent/stores/useAgentTelemetry';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { turns, setActiveSessionId, setActiveWorkspaceId, setPersistedMessages } from '@features/agent/stores/model';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';

function stubRuntime(merged: UnifiedChatMessage[]): void {
  registerAgentRuntimeContext({
    workspaceId: () => 'w1',
    providerId: () => 'p1',
    modelId: () => 'm1',
    maxImages: () => 4,
    findMessageContent: () => '',
    truncateHistory: () => Promise.resolve({ ok: true, replayText: '' }),
    loadSessionMessages: () => Promise.resolve(),
    refreshBudget: () => Promise.resolve(),
    touchSessionTime: () => undefined,
    generateTitle: () => undefined,
    reconcileTimes: () => undefined,
    askPending: () => false,
    clearAskPending: () => undefined,
    // 模拟后端已落盘：直接把消息追加进持久投影
    mergePersistedMessage: (_sid, msg) => {
      merged.push(msg as UnifiedChatMessage);
      setPersistedMessages([...merged]);
      return true;
    },
  });
}

function turn(kind: string, payload: Record<string, unknown> = {}): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId: 's1', kind, payload });
}
function textDelta(chunk: string): void {
  haloEventBus.emitLocal('llm:stream-event', { sessionId: 's1', kind: 'TextDelta', textDelta: chunk });
}

const assistantMsg = (id: string, content: string, callIds: string[]): UnifiedChatMessage =>
  ({
    id,
    role: 'assistant',
    content,
    createdAtUtc: '',
    toolCalls: callIds.map((c) => ({ id: c, type: 'function', function: { name: 'tool', arguments: '{}' } })),
  }) as unknown as UnifiedChatMessage;

const toolMsg = (id: string, callId: string): UnifiedChatMessage =>
  ({ id, role: 'tool', content: 'out', toolCallId: callId, createdAtUtc: '' }) as unknown as UnifiedChatMessage;

let merged: UnifiedChatMessage[] = [];

beforeEach(() => {
  setActivePinia(createPinia());
  turns.value = {};
  merged = [];
  stubRuntime(merged);
  setActiveSessionId('s1');
  setActiveWorkspaceId('w1');
  setPersistedMessages([]);
  // 绑定全部监听（幂等）。注意：AssistantMessagePersisted/TurnCompleted 等生命周期归约
  // 绑在 useAgentTurn 上，缺它的话 handleTurnLifecycle 根本不会被调用。
  useAgentTurn();
  useAgentStream();
  useAgentCards();
  useAgentTelemetry();
});

/** 等 rAF 合批（flushSegBuf）落定 */
async function settle(): Promise<void> {
  await new Promise((r) => setTimeout(r, 40));
}

describe('工具生命周期交接（need23-R7）', () => {
  it('工具落盘后：稳态折叠栏保留工具，活动容器让位（不再"收起来"）', async () => {
    const stream = useAgentStream();
    const telemetry = useAgentTelemetry();

    // 1) assistant 落盘（含正文 + tool_calls 声明）
    merged.push(assistantMsg('a1', '一行正文', ['c1']));
    setPersistedMessages([...merged]);
    turn('AssistantMessagePersisted', { iteration: 1 });
    await settle();

    // 2) 工具调用开始（活动容器出卡）
    turn('ToolCallStarted', { iteration: 1, callId: 'c1', functionName: 'list_dir', receivedBytes: 0 });
    await settle();
    expect(stream.visibleSegs.filter((s) => s.kind === 'tool')).toHaveLength(1);
    // 稳态此刻没有该 tool 消息，所以折叠栏也就没有工具
    expect(telemetry.visibleTurnGroups[0]?.toolMessages ?? []).toHaveLength(0);

    // 3) 工具回包落盘 → 应立刻移交稳态
    turn('ToolMessagePersisted', {
      iteration: 1,
      messageId: 't1',
      callId: 'c1',
      functionName: 'list_dir',
      toolCallId: 'c1',
      content: 'out',
      createdAtUtc: '2026-01-01T00:00:00Z',
    });
    await settle();

    // 活动容器：工具段已毕业
    expect(stream.visibleSegs.filter((s) => s.kind === 'tool')).toHaveLength(0);
    // 稳态：折叠栏拿到该工具（修复前这里恒为 0 → 整条折叠栏消失）
    const g = telemetry.visibleTurnGroups[0];
    expect(g?.toolMessages).toHaveLength(1);
    expect(String((g?.toolMessages[0] as unknown as { toolCallId?: string })?.toolCallId)).toBe('c1');
  });

  it('多轮：第 2 轮开始时不再把第 1 轮已落盘工具反复闪烁', async () => {
    const stream = useAgentStream();
    const telemetry = useAgentTelemetry();

    merged.push(assistantMsg('a1', '第一行', ['c1']));
    setPersistedMessages([...merged]);
    turn('AssistantMessagePersisted', { iteration: 1 });
    turn('ToolCallStarted', { iteration: 1, callId: 'c1', functionName: 'list_dir', receivedBytes: 0 });
    turn('ToolMessagePersisted', { iteration: 1, messageId: 't1', callId: 'c1', toolCallId: 'c1', content: 'out' });
    await settle();
    expect(telemetry.visibleTurnGroups[0]?.toolMessages).toHaveLength(1);

    // 第 2 轮开始：已落盘卡片应被清出 toolCards
    turn('IterationStarted', { iteration: 2 });
    await settle();
    expect(turns.value['s1']?.toolCards ?? []).toHaveLength(0);
    // 稳态仍保留第 1 轮工具
    expect(telemetry.visibleTurnGroups[0]?.toolMessages).toHaveLength(1);

    // 第 2 轮新工具出现 → 活动容器可见，且不影响第 1 轮稳态
    turn('ToolCallStarted', { iteration: 2, callId: 'c2', functionName: 'read_file', receivedBytes: 0 });
    await settle();
    expect(stream.visibleSegs.filter((s) => s.kind === 'tool')).toHaveLength(1);
    expect(telemetry.visibleTurnGroups[0]?.toolMessages).toHaveLength(1);
  });

  it('执行中（未落盘）的工具不应出现在稳态，避免与活动容器重复', async () => {
    const stream = useAgentStream();
    const telemetry = useAgentTelemetry();

    merged.push(assistantMsg('a1', '正文', ['c1']));
    merged.push(toolMsg('t1', 'c1')); // 假设后端已把 tool 写进 jsonl 但前端还没收到事件
    setPersistedMessages([...merged]);
    turn('AssistantMessagePersisted', { iteration: 1 });
    turn('ToolCallStarted', { iteration: 1, callId: 'c1', functionName: 'list_dir', receivedBytes: 0 });
    await settle();

    // 活动容器有卡
    expect(stream.visibleSegs.filter((s) => s.kind === 'tool')).toHaveLength(1);
    // 稳态去重隐藏（防止双份）
    const g = telemetry.visibleTurnGroups[0];
    expect(g?.toolMessages ?? []).toHaveLength(0);
  });

  it('正文流式段随 AssistantMessagePersisted 毕业，工具段不受影响', async () => {
    const stream = useAgentStream();
    turn('IterationStarted', { iteration: 1 });
    await settle(); // 让 curIteration=1 生效，后续文本段落归属该 iteration
    textDelta('流式正文');
    turn('ToolCallStarted', { iteration: 1, callId: 'c1', functionName: 'list_dir', receivedBytes: 0 });
    await settle();
    expect(stream.visibleSegs.filter((s) => s.kind === 'text')).toHaveLength(1);

    merged.push(assistantMsg('a1', '流式正文', ['c1']));
    setPersistedMessages([...merged]);
    turn('AssistantMessagePersisted', { iteration: 1 });
    await settle();

    // 正文段毕业；工具段仍在（等 ToolMessagePersisted）
    expect(stream.visibleSegs.filter((s) => s.kind === 'text')).toHaveLength(0);
    expect(stream.visibleSegs.filter((s) => s.kind === 'tool')).toHaveLength(1);
  });
});
