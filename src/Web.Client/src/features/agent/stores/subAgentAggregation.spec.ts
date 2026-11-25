/** 子代理状态聚合：事件序列驱动的 store 状态断言（后端改动前全部失败、改动后通过）。 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getBridgeTransport: () => ({}) as never, getHostBridge: () => ({}) as never };
});

import { haloEventBus } from '@shared/bridge/hostBridge';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';
import { ensureTurn, turns, setActiveSessionId, setActiveWorkspaceId } from '@features/agent/stores/model';
import { useAgentCards } from '@features/agent/stores/useAgentCards';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';

const MAIN = 'sess-main';

function stubRuntime(): void {
  registerAgentRuntimeContext({
    workspaceId: () => 'ws-main',
    providerId: () => 'prov-main',
    modelId: () => 'model-main',
    maxImages: () => 4,
    truncateHistory: () =>
      Promise.resolve({
        ok: true,
        replayText: '',
        replayImages: [],
        destructiveCommitCompleted: true,
        warnings: [],
      }),
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

/** 推一条 turn 事件；sessionId 默认父会话，子进度事件也可显式指定子桶验证错桶修复。 */
function emit(kind: string, payload: Record<string, unknown>, sessionId = MAIN): void {
  haloEventBus.emitLocal('agent:turn-event', { sessionId, kind, payload });
}

function started(subs: Array<{ subId: string; label: string }>, total: number): void {
  emit('SubAgentsStarted', { subs, total });
}

function items(): Array<{ subId: string; label: string; status: string }> {
  return ensureTurn(MAIN).subAgents.items;
}

function itemStatus(subId: string): string {
  return items().find((i) => i.subId === subId)?.status ?? '<missing>';
}

beforeEach(() => {
  vi.useFakeTimers();
  setActivePinia(createPinia());
  turns.value = {};
  stubRuntime();
  setActiveWorkspaceId('ws-main');
  setActiveSessionId(MAIN);
  useAgentCards(); // 绑定子代理 + 工具事件监听
  useAgentTurn(); // 绑定主轮终态（TurnCancelled 等）监听
});

afterEach(() => {
  vi.useRealTimers();
});

describe('子代理状态聚合', () => {
  it('并发子进度不互相覆盖', () => {
    started(
      [
        { subId: 'sess-a', label: '任务甲' },
        { subId: 'sess-b', label: '任务乙' },
      ],
      2
    );
    emit('SubAgentProgress', { subId: 'sess-a', status: 'running' });
    emit('SubAgentProgress', { subId: 'sess-b', status: 'running' });
    emit('SubAgentProgress', { subId: 'sess-a', status: 'succeeded' });

    const t = ensureTurn(MAIN).subAgents;
    expect(itemStatus('sess-a')).toBe('succeeded');
    expect(itemStatus('sess-b')).toBe('running');
    expect(t.done).toBe(1);
  });

  it('Released 保留取消语义而非压成 failed', () => {
    started(
      [
        { subId: 'sess-a', label: '任务甲' },
        { subId: 'sess-b', label: '任务乙' },
      ],
      2
    );
    emit('SubAgentsBarrierReleased', {
      subs: [
        { subId: 'sess-a', success: true },
        { subId: 'sess-b', success: false, errorCode: 'SUBAGENT_CANCELLED' },
      ],
    });

    expect(itemStatus('sess-a')).toBe('succeeded');
    expect(itemStatus('sess-b')).toBe('cancelled');
    expect(ensureTurn(MAIN).subAgents.done).toBe(2);
  });

  it('Released 保留超时语义并解除等待', () => {
    started(
      [
        { subId: 'sess-a', label: '任务甲' },
        { subId: 'sess-b', label: '任务乙' },
      ],
      2
    );
    emit('SubAgentsBarrierTimeout', { total: 2, timedOut: 1, subIds: ['sess-b'] });
    emit('SubAgentsBarrierReleased', {
      subs: [
        { subId: 'sess-a', success: true },
        { subId: 'sess-b', success: false, timedOut: true, errorCode: 'SUB_TIMEOUT' },
      ],
    });

    expect(itemStatus('sess-b')).toBe('timedOut');
    const t = ensureTurn(MAIN).subAgents;
    expect(t.done).toBe(2);
    expect(t.waiting).toBe(false);
  });

  it('取消后残留子项不卡住', () => {
    started([{ subId: 'sess-a', label: '任务甲' }], 1);
    emit('SubAgentsBarrier', { total: 1, waiting: true });
    emit('TurnCancelled', {});

    const t = ensureTurn(MAIN).subAgents;
    expect(itemStatus('sess-a')).toBe('cancelled');
    expect(t.waiting).toBe(false);
    expect(t.done).toBe(1);
  });

  it('多批 spawn 计数只增不减且四项齐全', () => {
    started(
      [
        { subId: 'sess-a', label: '任务甲' },
        { subId: 'sess-b', label: '任务乙' },
      ],
      2
    );
    emit('SubAgentsBarrierReleased', {
      subs: [
        { subId: 'sess-a', success: true },
        { subId: 'sess-b', success: true },
      ],
    });
    started(
      [
        { subId: 'sess-c', label: '任务丙' },
        { subId: 'sess-d', label: '任务丁' },
      ],
      2
    );
    // 第二批仍带 total=2（单批口径），但累计 total 不得回退。
    emit('SubAgentsBarrierReleased', {
      subs: [
        { subId: 'sess-c', success: true },
        { subId: 'sess-d', success: true },
      ],
    });

    const t = ensureTurn(MAIN).subAgents;
    expect(t.total).toBe(4);
    expect(t.done).toBe(4);
    expect(t.items.map((i) => i.subId).sort()).toEqual(['sess-a', 'sess-b', 'sess-c', 'sess-d']);
  });
});
