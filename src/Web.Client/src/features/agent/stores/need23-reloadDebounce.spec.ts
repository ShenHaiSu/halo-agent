/** need23-R4：全量重拉合并窗口 + finishTurn 时序单测（mock runtime）。 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getHostBridge: () => ({}) as never, getBridgeTransport: () => ({}) as never };
});

import { haloEventBus } from '@shared/bridge/hostBridge';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { turns, setActiveSessionId, setActiveWorkspaceId, ensureTurn, turnStartMarks } from '@features/agent/stores/model';

const loadSpy = vi.fn().mockResolvedValue(undefined);
const budgetSpy = vi.fn().mockResolvedValue(undefined);

function stubRuntime(): void {
  registerAgentRuntimeContext({
    workspaceId: () => 'w1',
    providerId: () => 'p1',
    modelId: () => 'm1',
    maxImages: () => 4,
    findMessageContent: () => '',
    truncateHistory: () => Promise.resolve({ ok: true, replayText: '' }),
    loadSessionMessages: loadSpy,
    refreshBudget: budgetSpy,
    touchSessionTime: () => undefined,
    generateTitle: () => undefined,
    reconcileTimes: () => undefined,
    askPending: () => false,
    clearAskPending: () => undefined,
    mergePersistedMessage: () => false,
  });
}

function emitTurn(kind: string, payload: Record<string, unknown> = {}, sessionId = 's1'): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId, kind, payload });
}

beforeEach(() => {
  vi.useFakeTimers();
  setActivePinia(createPinia());
  turns.value = {};
  loadSpy.mockClear();
  budgetSpy.mockClear();
  stubRuntime();
  setActiveSessionId('s1');
  setActiveWorkspaceId('w1');
  // 旧链断言（need23-R4）：显式关 p4-reload，锁定 persisted 全量重拉旧语义；
  // P4 新语义见本文件末尾 `p4-reload` 分组（开开关测）。
  try {
    window.localStorage.setItem('halo-need02-p4-reload', 'off');
  } catch {
    /* ignore */
  }
  useAgentTurn(); // 绑定监听（幂等）
});

afterEach(() => {
  try {
    window.localStorage.removeItem('halo-need02-p4-reload');
  } catch {
    /* ignore */
  }
  vi.useRealTimers();
});

describe('全量重拉合并窗口（need23-R4）', () => {
  it('两次 persisted 事件在 400ms 内只重拉一次', async () => {
    emitTurn('AssistantMessagePersisted', { iteration: 1 });
    emitTurn('AssistantMessagePersisted', { iteration: 2 });
    expect(loadSpy).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(450);
    expect(loadSpy).toHaveBeenCalledTimes(1);
  });

  it('persisted 后紧跟 TurnCompleted：finishTurn 立即重拉并取消待定定时器 → 仅 1 次', async () => {
    emitTurn('AssistantMessagePersisted', { iteration: 1 });
    // TurnCompleted 处理函数内部 void finishTurn(sid)（异步）
    emitTurn('TurnCompleted', { iterations: 1 });
    await vi.advanceTimersByTimeAsync(0); // 让 finishTurn 的 await 链推进
    await vi.advanceTimersByTimeAsync(450);
    expect(loadSpy).toHaveBeenCalledTimes(1);
  });

  it('仅 TurnCompleted（无 persisted）：仍重拉一次', async () => {
    emitTurn('TurnCompleted', { iterations: 1 });
    await vi.advanceTimersByTimeAsync(50);
    expect(loadSpy).toHaveBeenCalledTimes(1);
  });

  it('后台会话不发重拉（避免把 B 的消息刷进当前视图）', async () => {
    // 注意：sessionId 为空会回退到当前会话（resolveTargetSession 既有语义），此处必须给真实另一会话 id
    emitTurn('AssistantMessagePersisted', { iteration: 1 }, 's2');
    await vi.advanceTimersByTimeAsync(450);
    expect(loadSpy).not.toHaveBeenCalled();
  });

  it('finishTurn 后流式容器被清空', async () => {
    const t = ensureTurn('s1');
    t.streamingText = 'abc';
    t.segs = [{ key: 'k1', kind: 'text', iteration: 1, text: 'abc' }];
    emitTurn('TurnCancelled', { iteration: 1 });
    await vi.advanceTimersByTimeAsync(50);
    const after = ensureTurn('s1');
    expect(after.streamingText).toBe('');
    expect(after.segs).toHaveLength(0);
  });
});

describe('重拉收敛（need02-P4 p4-reload：persisted 只刷预算，全量收敛到轮末）', () => {
  beforeEach(() => {
    // 本组锁定 p2=on（轮末 defer 交接）+ p4=on（persisted 预算化）；G6 全关矩阵下回旧行为，见全关回归记录。
    try {
      window.localStorage.setItem('halo-need02-p2', 'on');
      window.localStorage.setItem('halo-need02-p4', 'on');
      window.localStorage.removeItem('halo-need02-p4-reload');
    } catch {
      /* ignore */
    }
  });
  afterEach(() => {
    try {
      window.localStorage.removeItem('halo-need02-p2');
      window.localStorage.removeItem('halo-need02-p4');
    } catch {
      /* ignore */
    }
  });

  it('两次 persisted 事件：全量 0 次，预算防抖后 1 次', async () => {
    emitTurn('AssistantMessagePersisted', { iteration: 1 });
    emitTurn('AssistantMessagePersisted', { iteration: 2 });
    expect(loadSpy).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(450);
    expect(loadSpy).not.toHaveBeenCalled();
    expect(budgetSpy).toHaveBeenCalledTimes(1);
  });

  it('persisted 后紧跟 TurnCompleted：全量仅轮末 1 次', async () => {
    emitTurn('AssistantMessagePersisted', { iteration: 1 });
    emitTurn('TurnCompleted', { iterations: 1 });
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(450);
    expect(loadSpy).toHaveBeenCalledTimes(1);
  });

  it('TurnCompleted 立起交接窗（liveCompleting），300ms 后清容器并落窗', async () => {
    const t = ensureTurn('s1');
    t.streamingText = 'abc';
    t.segs = [{ key: 'k1', kind: 'text', iteration: 1, text: 'abc' }];
    emitTurn('TurnCompleted', { iterations: 1 });
    await vi.advanceTimersByTimeAsync(0);
    const completing = ensureTurn('s1');
    // 交接窗内：流容器保留（内嵌 LiveBody 有依据渲染，无消失帧）。
    expect(completing.liveCompleting).toBe(true);
    expect(completing.segs).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(300);
    const after = ensureTurn('s1');
    expect(after.liveCompleting).toBe(false);
    expect(after.streamingText).toBe('');
    expect(after.segs).toHaveLength(0);
  });

  it('交接窗内起新轮：旧延迟清容器被拦截（不 wiped 新轮）', async () => {
    const t = ensureTurn('s1');
    t.segs = [{ key: 'k-old', kind: 'text', iteration: 1, text: 'old' }];
    emitTurn('TurnCompleted', { iterations: 1 });
    await vi.advanceTimersByTimeAsync(0);
    expect(ensureTurn('s1').liveCompleting).toBe(true);
    // 模拟窗内新发送：turnStartMarks 前进 + 新容器内容（sendPrompt 内联语义）。
    turnStartMarks['s1'] = Date.now() + 1000;
    const cur = ensureTurn('s1');
    cur.liveCompleting = false;
    cur.segs = [{ key: 'k-new', kind: 'text', iteration: 1, text: 'new' }];
    await vi.advanceTimersByTimeAsync(300);
    const after = ensureTurn('s1');
    expect(after.segs).toHaveLength(1);
    expect(after.segs[0]?.key).toBe('k-new');
  });
});
