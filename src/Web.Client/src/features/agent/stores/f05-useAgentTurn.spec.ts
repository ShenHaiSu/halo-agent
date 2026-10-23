/** need21-F05: useAgentTurn.send 单测（mock api + runtime 上下文，不触真实桥）。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/api/agent', () => ({
  startAgentTurn: vi.fn(),
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return {
    ...orig,
    getBridgeTransport: () => ({}) as never,
  };
});

import { startAgentTurn } from '@shared/bridge/api/agent';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { activeSessionId, activeWorkspaceId, setActiveSessionId, setActiveWorkspaceId, turns } from '@features/agent/stores/model';

const mockedStart = vi.mocked(startAgentTurn);

function stubRuntime(): void {
  registerAgentRuntimeContext({
    workspaceId: () => 'w1',
    providerId: () => 'p1',
    modelId: () => 'm1',
    maxImages: () => 4,
    findMessageContent: () => '',
    truncateHistory: () => Promise.resolve({ ok: true, replayText: 'replay' }),
    loadSessionMessages: () => Promise.resolve(),
    refreshBudget: () => Promise.resolve(),
    touchSessionTime: () => undefined,
    generateTitle: () => undefined,
    reconcileTimes: () => undefined,
    askPending: () => false,
    clearAskPending: () => undefined,
    mergePersistedMessage: () => false,
  });
}

beforeEach(() => {
  setActivePinia(createPinia());
  turns.value = {};
  mockedStart.mockReset();
  stubRuntime();
  setActiveSessionId('s1');
  setActiveWorkspaceId('w1');
  void activeSessionId;
  void activeWorkspaceId;
});

describe('useAgentTurn.sendPrompt（mock api）', () => {
  it('空文本/无会话拒绝发送', async () => {
    const store = useAgentTurn();
    expect(await store.sendPrompt('   ')).toBe(false);
    expect(mockedStart).not.toHaveBeenCalled();
    setActiveSessionId('');
    expect(await store.sendPrompt('hello')).toBe(false);
  });
  it('成功置 isGenerating 并经 api 发送（含 requestId）', async () => {
    mockedStart.mockResolvedValue({ ok: true, code: '', message: '', data: 't1' });
    const store = useAgentTurn();
    const ok = await store.sendPrompt('hello');
    expect(ok).toBe(true);
    expect(mockedStart).toHaveBeenCalledTimes(1);
    const params = mockedStart.mock.calls[0]?.[1] as unknown as Record<string, unknown>;
    expect(params['sessionId']).toBe('s1');
    expect(params['userText']).toBe('hello');
    expect(String(params['requestId'] || '')).not.toBe('');
  });
  it('后端 TURN_IN_PROGRESS 恢复旧轮并返回 false', async () => {
    mockedStart.mockResolvedValue({ ok: false, code: 'TURN_IN_PROGRESS', message: 'busy' });
    const store = useAgentTurn();
    expect(await store.sendPrompt('hello')).toBe(false);
  });
  it('连点第二发直接忽略（不起新轮）', async () => {
    mockedStart.mockResolvedValue({ ok: true, code: '', message: '', data: 't1' });
    const store = useAgentTurn();
    expect(await store.sendPrompt('first')).toBe(true);
    // 首轮仍标记 generating（无 TurnCompleted 事件），第二发应被忽略
    expect(await store.sendPrompt('second')).toBe(false);
    expect(mockedStart).toHaveBeenCalledTimes(1);
  });
});
