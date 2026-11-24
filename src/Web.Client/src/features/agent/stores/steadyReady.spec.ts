/** 稳态就绪信号：初始值、轮末对账时序、失败重试路径（mock runtime + 可控 reload）。 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { haloEventBus } from '@shared/bridge/hostBridge';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { emptyTurnState, turns, setActiveSessionId, setActiveWorkspaceId, ensureTurn } from '@features/agent/stores/model';

let resolveLoad: (() => void) | null = null;
const loadSpy = vi.fn(
  () =>
    new Promise<void>((resolve) => {
      resolveLoad = resolve;
    })
);
const budgetSpy = vi.fn().mockResolvedValue(undefined);

function emitTurn(kind: string, payload: Record<string, unknown> = {}, sessionId = 's1'): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId, kind, payload });
}

beforeEach(() => {
  vi.useFakeTimers();
  setActivePinia(createPinia());
  turns.value = {};
  resolveLoad = null;
  loadSpy.mockClear();
  budgetSpy.mockClear();
  registerAgentRuntimeContext({
    workspaceId: () => 'w1',
    providerId: () => 'provider-a',
    modelId: () => 'm1',
    maxImages: () => 4,
    truncateHistory: () =>
      Promise.resolve({
        ok: true,
        replayText: '',
        replayImages: [],
        destructiveCommitCompleted: true,
        warnings: [],
      }),
    loadSessionMessages: loadSpy,
    refreshBudget: budgetSpy,
    touchSessionTime: () => undefined,
    generateTitle: () => undefined,
    reconcileTimes: () => undefined,
    askPending: () => false,
    clearAskPending: () => undefined,
    mergePersistedMessage: () => false,
  });
  setActiveSessionId('s1');
  setActiveWorkspaceId('w1');
  useAgentTurn();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('稳态就绪信号', () => {
  it('EmptyTurn_StartsWithSteadyNotReady', () => {
    expect(emptyTurnState('s1').steadyReady).toBe(false);
  });

  it('Completion_KeepsSteadyNotReadyUntilReloadReturns', async () => {
    const t = ensureTurn('s1');
    t.streamingText = 'answer';
    t.segs = [{ key: 'k1', kind: 'text', iteration: 1, text: 'answer' }];
    emitTurn('TurnCompleted', { iterations: 1 });
    await vi.advanceTimersByTimeAsync(0);
    const during = ensureTurn('s1');
    expect(during.liveCompleting).toBe(true);
    expect(during.steadyReady).toBe(false);
    expect(typeof resolveLoad).toBe('function');
    resolveLoad?.();
    await vi.advanceTimersByTimeAsync(0);
    expect(ensureTurn('s1').steadyReady).toBe(true);
    await vi.advanceTimersByTimeAsync(300);
    expect(ensureTurn('s1').steadyReady).toBe(false);
    expect(ensureTurn('s1').liveCompleting).toBe(false);
  });

  it('FailureRetry_DoesNotMarkSteadyReady', async () => {
    emitTurn('TurnError', { errorCode: 'E', errorMessage: 'boom', retry: true });
    await vi.advanceTimersByTimeAsync(0);
    resolveLoad?.();
    await vi.advanceTimersByTimeAsync(350);
    expect(ensureTurn('s1').steadyReady).toBe(false);
    expect(ensureTurn('s1').liveCompleting).toBe(false);
  });
});
