/**
 * 第二阶段回归：首回复前取消的 pending 对账守卫。
 * 覆盖：摘要命中即清 / 未命中保留 cancelled / 预取舍消删除 / 有界重试 / 切会话与新发送守卫。
 * 测试 id 统一语义词+连字符形态。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { haloEventBus } from '@shared/bridge/hostBridge';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { finishTurn, stopGenerating } from '@features/agent/stores/turnEvents';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { ensureTurn, setActiveSessionId, setActiveWorkspaceId, turnStartMarks, turns } from '@features/agent/stores/model';
import { isPendingCancelled } from '@features/session/utils/pendingUser';

const loadSpy = vi.fn().mockResolvedValue(undefined);
const budgetSpy = vi.fn().mockResolvedValue(undefined);
let summaryHit = false;

function stubRuntime(): void {
  registerAgentRuntimeContext({
    workspaceId: () => 'workspace-cancel',
    providerId: () => 'provider-cancel',
    modelId: () => 'model-cancel',
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
    hasTurnSummary: () => summaryHit,
    mergePersistedMessage: () => false,
  });
}

function emitTurn(kind: string, payload: Record<string, unknown> = {}, sessionId = 'sess-cancel'): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId, kind, payload });
}

function seedPending(sessionId = 'sess-cancel', requestId = 'request-cancel'): void {
  const t = ensureTurn(sessionId);
  t.pendingUser = {
    key: `pending-${requestId}`,
    requestId,
    sessionId,
    content: 'first question',
    images: [],
    previews: {},
    createdAtUtc: new Date().toISOString(),
    status: 'sending',
    errorCode: null,
    confirmedId: null,
  };
  turnStartMarks[sessionId] = Date.now();
}

beforeEach(() => {
  vi.useFakeTimers();
  setActivePinia(createPinia());
  turns.value = {};
  loadSpy.mockClear();
  budgetSpy.mockClear();
  summaryHit = false;
  stubRuntime();
  setActiveSessionId('sess-cancel');
  setActiveWorkspaceId('workspace-cancel');
  useAgentTurn();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('取消对账守卫', () => {
  it('摘要命中时取消 pending 删除且无重复气泡', async () => {
    seedPending();
    summaryHit = true;
    emitTurn('TurnCancelled', { iteration: 1, userMessagePersisted: true, timelineFinalized: true });
    await vi.advanceTimersByTimeAsync(50);
    expect(ensureTurn('sess-cancel').pendingUser).toBeNull();
  });

  it('摘要未命中时取消 pending 保留并显示 cancelled', async () => {
    seedPending();
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 1, userMessagePersisted: true, timelineFinalized: false });
    await vi.advanceTimersByTimeAsync(50);
    const pending = ensureTurn('sess-cancel').pendingUser;
    expect(pending).not.toBeNull();
    expect(isPendingCancelled(pending)).toBe(true);
    expect(pending?.errorCode).toBe('TURN_CANCELLED');
  });

  it('预取舍消直接删除 pending，不安排重试', async () => {
    seedPending();
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 0, reason: 'cancelled-before-start', userMessagePersisted: false, timelineFinalized: false });
    await vi.advanceTimersByTimeAsync(50);
    expect(ensureTurn('sess-cancel').pendingUser).toBeNull();
    await vi.advanceTimersByTimeAsync(2000);
    expect(loadSpy).toHaveBeenCalledTimes(1);
  });

  it('旧后端字段缺省时保守保留 cancelled', async () => {
    seedPending();
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 1 });
    await vi.advanceTimersByTimeAsync(50);
    expect(isPendingCancelled(ensureTurn('sess-cancel').pendingUser)).toBe(true);
  });

  it('重拉抛错时 pending 不删除并记录诊断', async () => {
    seedPending();
    summaryHit = false;
    loadSpy.mockRejectedValueOnce(new Error('bridge busy'));
    await finishTurn('sess-cancel', { terminalStatus: 'cancelled', userMessagePersisted: true, timelineFinalized: true });
    expect(isPendingCancelled(ensureTurn('sess-cancel').pendingUser)).toBe(true);
  });

  it('旧轮 finish 不清理新轮 pending', async () => {
    seedPending('sess-cancel', 'request-old');
    summaryHit = true;
    let resolveLoad: (() => void) | undefined;
    loadSpy.mockImplementationOnce(
      () =>
        new Promise<void>((resolve) => {
          resolveLoad = resolve;
        })
    );
    const pending = finishTurn('sess-cancel', { terminalStatus: 'cancelled', userMessagePersisted: true, timelineFinalized: true });
    await vi.advanceTimersByTimeAsync(0);
    // 旧 finish 仍在重拉时新发送：新 pending + 新 mark，旧收尾不得再动新轮。
    seedPending('sess-cancel', 'request-new');
    turnStartMarks['sess-cancel'] = (turnStartMarks['sess-cancel'] || 0) + 1000;
    if (resolveLoad) resolveLoad();
    await pending;
    expect(ensureTurn('sess-cancel').pendingUser?.requestId).toBe('request-new');
  });
});

describe('有界对账重试', () => {
  it('第二次命中后停止，不再执行第三次', async () => {
    seedPending();
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 1, userMessagePersisted: true, timelineFinalized: false });
    await vi.advanceTimersByTimeAsync(50);
    expect(loadSpy).toHaveBeenCalledTimes(1);
    summaryHit = true;
    await vi.advanceTimersByTimeAsync(600);
    expect(ensureTurn('sess-cancel').pendingUser).toBeNull();
    const callsAfterHit = loadSpy.mock.calls.length;
    await vi.advanceTimersByTimeAsync(2000);
    expect(loadSpy.mock.calls.length).toBe(callsAfterHit);
  });

  it('耗尽后保留 cancelled', async () => {
    seedPending();
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 1, userMessagePersisted: true, timelineFinalized: false });
    await vi.advanceTimersByTimeAsync(50);
    await vi.advanceTimersByTimeAsync(600);
    await vi.advanceTimersByTimeAsync(1600);
    await vi.advanceTimersByTimeAsync(100);
    expect(isPendingCancelled(ensureTurn('sess-cancel').pendingUser)).toBe(true);
  });

  it('切会话停止旧重试', async () => {
    seedPending();
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 1, userMessagePersisted: true, timelineFinalized: false });
    await vi.advanceTimersByTimeAsync(50);
    setActiveSessionId('sess-other');
    const calls = loadSpy.mock.calls.length;
    await vi.advanceTimersByTimeAsync(2000);
    expect(loadSpy.mock.calls.length).toBe(calls);
  });

  it('新发送停止旧重试', async () => {
    seedPending('sess-cancel', 'request-old');
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 1, userMessagePersisted: true, timelineFinalized: false });
    await vi.advanceTimersByTimeAsync(50);
    turnStartMarks['sess-cancel'] = Date.now() + 5000;
    const calls = loadSpy.mock.calls.length;
    await vi.advanceTimersByTimeAsync(2000);
    expect(loadSpy.mock.calls.length).toBe(calls);
  });

  it('后台会话不刷当前视图', async () => {
    seedPending('sess-cancel', 'request-cancel');
    summaryHit = false;
    emitTurn('TurnCancelled', { iteration: 1, userMessagePersisted: true, timelineFinalized: false }, 'sess-other');
    await vi.advanceTimersByTimeAsync(50);
    expect(loadSpy).not.toHaveBeenCalled();
    expect(isPendingCancelled(ensureTurn('sess-cancel').pendingUser)).toBe(false);
    expect(ensureTurn('sess-cancel').pendingUser?.status).toBe('sending');
  });
});

describe('停止超时降级', () => {
  it('5 秒未收到取消事件时保留 cancelled 并显示确认中', async () => {
    seedPending('sess-cancel', 'request-cancel');
    const t = ensureTurn('sess-cancel');
    t.requestId = 'request-cancel';
    const pending = stopGenerating('sess-cancel');
    await vi.advanceTimersByTimeAsync(5000);
    await pending;
    const after = ensureTurn('sess-cancel');
    expect(isPendingCancelled(after.pendingUser)).toBe(true);
    expect(after.pendingUser?.errorCode).toBe('CANCEL_CONFIRM_TIMEOUT');
    expect(after.turnNotice).toContain('正在确认历史状态');
  });
});
