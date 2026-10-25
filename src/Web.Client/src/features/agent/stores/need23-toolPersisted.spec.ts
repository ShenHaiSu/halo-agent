/** need23-R1：ToolMessagePersisted 增量合并单测（mock runtime，不触真实桥）。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getBridgeTransport: () => ({}) as never };
});

import { haloEventBus } from '@shared/bridge/hostBridge';
import { useAgentCards } from '@features/agent/stores/useAgentCards';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { turns, setActiveSessionId, setActiveWorkspaceId, ensureTurn } from '@features/agent/stores/model';

const mergeSpy = vi.fn().mockReturnValue(true);

function stubRuntime(): void {
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
    mergePersistedMessage: mergeSpy,
  });
}

function emitTurn(kind: string, payload: Record<string, unknown>, sessionId = 's1'): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId, kind, payload });
}

beforeEach(() => {
  setActivePinia(createPinia());
  turns.value = {};
  mergeSpy.mockClear();
  mergeSpy.mockReturnValue(true);
  stubRuntime();
  setActiveSessionId('s1');
  setActiveWorkspaceId('w1');
  useAgentCards(); // 绑定事件监听（幂等）
});

describe('ToolMessagePersisted（need23-R1）', () => {
  it('到达后合并进视图一次，并标记卡片已落盘', () => {
    const t = ensureTurn('s1');
    t.toolCards.push({
      callId: 'c1',
      functionName: 'list_dir',
      argsJson: '{}',
      status: 'running',
      resultText: '',
      exitCode: null,
      durationMs: null,
      iteration: 1,
      receivedBytes: 0,
    });

    emitTurn('ToolMessagePersisted', {
      iteration: 1,
      messageId: 'msg_t1',
      callId: 'c1',
      functionName: 'list_dir',
      toolCallId: 'c1',
      content: 'out',
      createdAtUtc: '2026-01-01T00:00:00Z',
    });

    expect(mergeSpy).toHaveBeenCalledTimes(1);
    const [sid, msg] = mergeSpy.mock.calls[0] as [string, Record<string, unknown>];
    expect(sid).toBe('s1');
    expect(msg['id']).toBe('msg_t1');
    expect(msg['role']).toBe('tool');
    expect(msg['toolCallId']).toBe('c1');
    expect(t.toolCards[0]?.messageId).toBe('msg_t1');
    expect(t.toolCards[0]?.persisted).toBe(true);
  });

  it('toolCallId 缺失时回退 callId', () => {
    emitTurn('ToolMessagePersisted', {
      iteration: 1,
      messageId: 'msg_t2',
      callId: 'c2',
      functionName: 'read_file',
      content: 'x',
    });
    const msg = mergeSpy.mock.calls[0]?.[1] as Record<string, unknown>;
    expect(msg['toolCallId']).toBe('c2');
  });

  it('合并抛错不影响事件归约（不打断后续卡片状态）', () => {
    mergeSpy.mockImplementation(() => {
      throw new Error('boom');
    });
    const t = ensureTurn('s1');
    expect(() => emitTurn('ToolMessagePersisted', { iteration: 1, messageId: 'm', callId: 'c9', content: 'c' })).not.toThrow();
    expect(t.updatedAt).toBeGreaterThan(0);
  });

  it('后台会话事件按 sessionId 落自己的桶', () => {
    emitTurn('ToolMessagePersisted', { iteration: 1, messageId: 'mb', callId: 'cb', content: 'c' }, 's2');
    expect(mergeSpy).toHaveBeenCalledWith('s2', expect.any(Object));
  });
});

describe('S6 §4.4：ToolMessagePersisted 错误码透传', () => {
  function pushCard(callId: string, functionName = 'view_image'): void {
    const t = ensureTurn('s1');
    t.toolCards.push({
      callId,
      functionName,
      argsJson: '{}',
      status: 'running',
      resultText: '',
      exitCode: null,
      durationMs: null,
      iteration: 1,
      receivedBytes: 0,
    });
  }

  it('带 errorCode ⇒ 卡片置 error 且记录四态码', () => {
    pushCard('c_img');
    emitTurn('ToolMessagePersisted', {
      iteration: 1,
      messageId: 'msg_err',
      callId: 'c_img',
      functionName: 'view_image',
      toolCallId: 'c_img',
      content: 'IMAGE_MISSING：文件不存在',
      errorCode: 'IMAGE_MISSING',
      createdAtUtc: '2026-01-01T00:00:00Z',
    });
    const card = ensureTurn('s1').toolCards[0];
    expect(card.errorCode).toBe('IMAGE_MISSING');
    expect(card.status).toBe('error');
    expect(card.persisted).toBe(true);
  });

  it('不带 errorCode ⇒ 不改状态（不与成功态冲突）', () => {
    pushCard('c_ok');
    const t = ensureTurn('s1');
    t.toolCards[0].status = 'done';
    emitTurn('ToolMessagePersisted', {
      iteration: 1,
      messageId: 'msg_ok',
      callId: 'c_ok',
      functionName: 'view_image',
      toolCallId: 'c_ok',
      content: '已读取 a.png',
      createdAtUtc: '2026-01-01T00:00:00Z',
    });
    const card = ensureTurn('s1').toolCards[0];
    expect(card.errorCode).toBeUndefined();
    expect(card.status).toBe('done');
  });
});
