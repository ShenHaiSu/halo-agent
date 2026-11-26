import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { activeSessionId, ensureTurn, emptyTurnState, setActiveSessionId, turns } from './model';
import { handleTurnLifecycle, isBusyForHistory, isCompacting } from './turnEvents';
import { registerAgentRuntimeContext } from './runtime';

const loadSessionMessages = vi.fn(async () => undefined);
const refreshBudget = vi.fn(async () => undefined);

describe('turnEvents compact 状态契约', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    turns.value = {};
    setActiveSessionId('session-compact');
    void activeSessionId;
    loadSessionMessages.mockClear();
    refreshBudget.mockClear();
    registerAgentRuntimeContext({
      loadSessionMessages,
      refreshBudget,
      touchSessionTime: () => undefined,
      askPending: () => false,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('Started 置显式忙态，且不依赖提示文案', () => {
    handleTurnLifecycle('CompactStarted', {}, 'session-compact');
    const turn = ensureTurn('session-compact');
    expect(turn.compacting).toBe(true);
    expect(turn.streamingActive).toBe(false);
    expect(isCompacting('session-compact')).toBe(true);
    turn.turnNotice = '任意文案';
    expect(isCompacting('session-compact')).toBe(true);
    expect(isBusyForHistory('session-compact')).toBe(true);
  });

  it('NoOp 清理忙态，只刷预算不重拉 timeline', async () => {
    handleTurnLifecycle('CompactStarted', {}, 'session-compact');
    handleTurnLifecycle('CompactNoOp', { noOpReason: 'TOO_SHORT' }, 'session-compact');
    await Promise.resolve();

    const turn = ensureTurn('session-compact');
    expect(turn.compacting).toBe(false);
    expect(turn.isGenerating).toBe(false);
    expect(turn.turnNotice).toContain('历史还太短');
    expect(refreshBudget).toHaveBeenCalledTimes(1);
    expect(loadSessionMessages).not.toHaveBeenCalled();
  });

  it('Completed 清理忙态并刷新 timeline 与预算', async () => {
    handleTurnLifecycle('CompactStarted', {}, 'session-compact');
    handleTurnLifecycle('CompactCompleted', { removedMessages: 12, summaryTokens: 80 }, 'session-compact');
    await vi.advanceTimersByTimeAsync(500);

    const turn = ensureTurn('session-compact');
    expect(turn.compacting).toBe(false);
    expect(turn.isGenerating).toBe(false);
    expect(turn.turnNotice).toContain('已压缩');
    expect(loadSessionMessages).toHaveBeenCalledTimes(1);
    expect(refreshBudget).toHaveBeenCalledTimes(1);
  });

  it('Failed 清理忙态并显示错误', () => {
    handleTurnLifecycle('CompactStarted', {}, 'session-compact');
    handleTurnLifecycle('CompactFailed', { errorCode: 'COMPACT_LLM_ERROR', errorMessage: '模型失败' }, 'session-compact');
    const turn = ensureTurn('session-compact');
    expect(turn.compacting).toBe(false);
    expect(turn.turnNotice).toContain('模型失败');
    expect(turn.lastErrorInfo?.retry).toBe(false);
  });

  it('新轮状态显式初始化 compacting=false', () => {
    expect(emptyTurnState('session-compact').compacting).toBe(false);
  });
});
