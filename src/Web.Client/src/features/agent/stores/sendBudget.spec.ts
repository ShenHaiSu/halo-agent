/** 发送链预算透传回归（业务语义命名）。 */
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
import { haloEventBus } from '@shared/bridge/hostBridge';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentTelemetry } from '@features/agent/stores/useAgentTelemetry';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { activeSessionId, activeWorkspaceId, setActiveSessionId, setActiveWorkspaceId, turns } from '@features/agent/stores/model';

const mockedStart = vi.mocked(startAgentTurn);
const loadSpy = vi.fn().mockResolvedValue(undefined);
const patchSpy = vi.fn();
const budgetSpy = vi.fn().mockResolvedValue(undefined);

function stubRuntime(): void {
  registerAgentRuntimeContext({
    workspaceId: () => 'w1',
    providerId: () => 'p1',
    modelId: () => 'm1',
    maxImages: () => 4,
    truncateHistory: () => Promise.resolve({ ok: true, replayText: 'replay' }),
    loadSessionMessages: loadSpy,
    applyUsagePatch: patchSpy,
    refreshBudget: budgetSpy,
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
  loadSpy.mockClear();
  patchSpy.mockClear();
  budgetSpy.mockClear();
  mockedStart.mockResolvedValue({ ok: true, code: '', message: '', data: 't1' });
  stubRuntime();
  setActiveSessionId('s1');
  setActiveWorkspaceId('w1');
  void activeSessionId;
  void activeWorkspaceId;
});

describe('发送链预算语义', () => {
  it('sendKeepsBudgetThroughRuntime', async () => {
    const store = useAgentTurn();
    expect(await store.sendPrompt('hello')).toBe(true);
    expect(loadSpy).toHaveBeenCalledTimes(1);
    expect(loadSpy.mock.calls[0]?.[1]).toMatchObject({ preserveBudget: true });
  });

  it('replayUserStillClearsBudget', async () => {
    const store = useAgentTurn();
    expect(await store.replayUserMessage('m1')).toBe(true);
    expect(loadSpy).toHaveBeenCalledTimes(2);
    expect(loadSpy.mock.calls[0]?.[1]).toBeUndefined();
    expect(loadSpy.mock.calls[1]?.[1]).toMatchObject({ preserveBudget: true });
  });

  it('replayAssistantStillClearsBudget', async () => {
    const store = useAgentTurn();
    expect(await store.replayAssistantTurn('m2')).toBe(true);
    expect(loadSpy).toHaveBeenCalledTimes(2);
    expect(loadSpy.mock.calls[0]?.[1]).toBeUndefined();
    expect(loadSpy.mock.calls[1]?.[1]).toMatchObject({ preserveBudget: true });
  });

  it('usageEventForwardsPatchWithoutBackend', () => {
    useAgentTelemetry();
    haloEventBus.emitLocal('llm:stream-event', {
      sessionId: 's1',
      kind: 'UsageUpdated',
      usage: { inputTokens: 1500, outputTokens: 10, cachedInputTokens: 300, totalTokens: 1810 },
    });
    expect(patchSpy).toHaveBeenCalledTimes(1);
    expect(patchSpy.mock.calls[0]?.[0]).toBe('s1');
    expect(patchSpy.mock.calls[0]?.[1]).toMatchObject({ inputTokens: 1500 });
    expect(budgetSpy).not.toHaveBeenCalled();
  });
});
