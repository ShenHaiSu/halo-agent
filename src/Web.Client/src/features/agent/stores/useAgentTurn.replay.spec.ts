import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

vi.mock('@shared/bridge/api/agent', () => ({ startAgentTurn: vi.fn() }));
vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const orig = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...orig, getBridgeTransport: () => ({}) as never };
});

import { startAgentTurn } from '@shared/bridge/api/agent';
import { setHistoryCapabilitiesState } from '@shared/bridge/hostBridge';
import { useAgentTurn } from './useAgentTurn';
import { registerAgentRuntimeContext } from './runtime';
import { setActiveSessionId, setActiveWorkspaceId, turns } from './model';

const start = vi.mocked(startAgentTurn);
const truncate = vi.fn();
const loadMessages = vi.fn().mockResolvedValue(undefined);
const refreshBudget = vi.fn().mockResolvedValue(undefined);

const supported = {
  historyAnchors: true,
  replayImages: true,
  atomicHistoryMutex: true,
  strictBranchRollback: true,
};

function replayResult(over: Record<string, unknown> = {}) {
  return {
    ok: true,
    replayText: '原始提问',
    replayImages: [],
    destructiveCommitCompleted: true,
    recoveryToken: 'replay:sess-a:u1',
    warnings: [],
    ...over,
  };
}

beforeEach(() => {
  setActivePinia(createPinia());
  turns.value = {};
  start.mockReset();
  truncate.mockReset();
  loadMessages.mockReset();
  refreshBudget.mockReset();
  loadMessages.mockResolvedValue(undefined);
  refreshBudget.mockResolvedValue(undefined);
  start.mockResolvedValue({ ok: true, code: '', message: '', data: 'request-1' });
  truncate.mockResolvedValue(replayResult());
  setHistoryCapabilitiesState(supported);
  registerAgentRuntimeContext({
    workspaceId: () => 'w1',
    providerId: () => 'p1',
    modelId: () => 'm1',
    maxImages: () => 4,
    truncateHistory: truncate,
    loadSessionMessages: loadMessages,
    refreshBudget,
    applyUsagePatch: () => undefined,
    touchSessionTime: () => undefined,
    generateTitle: () => undefined,
    reconcileTimes: () => undefined,
    askPending: () => false,
    clearAskPending: () => undefined,
    mergePersistedMessage: () => false,
  });
  setActiveWorkspaceId('w1');
  setActiveSessionId('sess-a');
});

describe('重放截断状态机', () => {
  it('传入真实消息锚点，并把可用图片作为 stored 媒体重发', async () => {
    truncate.mockResolvedValue(
      replayResult({
        replayImages: [
          { imageId: 'img_1', fileName: 'a.png', mime: 'image/png', lost: false },
          { imageId: 'img_2', fileName: 'b.png', mime: 'image/png', lost: true },
        ],
      })
    );
    const store = useAgentTurn();
    const result = await store.replayUserMessage('u1');
    expect(result.ok).toBe(true);
    expect(truncate).toHaveBeenCalledWith('w1', 'sess-a', 'u1', true);
    expect(start).toHaveBeenCalledTimes(1);
    const payload = start.mock.calls[0]?.[1] as { attachmentsJson?: string; sessionId: string };
    expect(payload.sessionId).toBe('sess-a');
    expect(JSON.parse(payload.attachmentsJson || '[]')).toEqual([
      { kind: 'stored', imageId: 'img_1', fileName: 'a.png', mime: 'image/png' },
    ]);
    expect(result.warnings?.some((w) => w.code === 'IMAGE_MISSING')).toBe(true);
  });

  it('截断失败回传稳定错误码，不起新轮', async () => {
    truncate.mockResolvedValue({ ok: false, code: 'ANCHOR_NOT_FOUND', message: '历史已变化' });
    const result = await useAgentTurn().replayUserMessage('u1');
    expect(result.ok).toBe(false);
    expect(result.code).toBe('ANCHOR_NOT_FOUND');
    expect(result.message).toContain('刷新');
    expect(start).not.toHaveBeenCalled();
  });

  it('成功响应缺少提交标记时按提交不确定处理，不发送', async () => {
    truncate.mockResolvedValue(replayResult({ destructiveCommitCompleted: false }));
    const result = await useAgentTurn().replayAssistantTurn('a2');
    expect(result.code).toBe('TRUNCATE_COMMIT_ERROR');
    expect(start).not.toHaveBeenCalled();
  });

  it('截断后切换会话：不发送、保留恢复项并明确提示', async () => {
    loadMessages.mockImplementation(async () => {
      setActiveSessionId('other');
    });
    const store = useAgentTurn();
    const result = await store.replayUserMessage('u1');
    expect(result.code).toBe('REPLAY_TARGET_CHANGED');
    expect(result.message).toContain('历史已截断');
    expect(start).not.toHaveBeenCalled();
    expect(turns.value['sess-a']?.pendingReplay?.text).toBe('原始提问');
  });

  it('刷新失败保留恢复项且不发送', async () => {
    loadMessages.mockRejectedValue(new Error('refresh failed'));
    const store = useAgentTurn();
    const result = await store.replayUserMessage('u1');
    expect(result.code).toBe('REPLAY_SEND_FAILED');
    expect(result.message).toContain('稍后继续');
    expect(start).not.toHaveBeenCalled();
    expect(turns.value['sess-a']?.pendingReplay).not.toBeNull();
  });

  it('发送失败保留恢复项，恢复发送使用新 requestId', async () => {
    start.mockResolvedValueOnce({ ok: false, code: 'AGENT_START_ERROR', message: '发送失败', data: '' });
    const store = useAgentTurn();
    const first = await store.replayUserMessage('u1');
    expect(first.code).toBe('REPLAY_SEND_FAILED');
    expect(turns.value['sess-a']?.pendingReplay).not.toBeNull();
    start.mockResolvedValue({ ok: true, code: '', message: '', data: 'request-2' });
    const resumed = await store.resumePendingReplay();
    expect(resumed.ok).toBe(true);
    expect(start).toHaveBeenCalledTimes(2);
    const firstId = (start.mock.calls[0]?.[1] as { requestId: string }).requestId;
    const secondId = (start.mock.calls[1]?.[1] as { requestId: string }).requestId;
    expect(secondId).not.toBe(firstId);
    expect(turns.value['sess-a']?.pendingReplay).toBeNull();
  });

  it('恢复入口只在目标会话显示，切走后不发送', async () => {
    loadMessages.mockImplementation(async () => {
      setActiveSessionId('other');
    });
    const store = useAgentTurn();
    await store.replayUserMessage('u1');
    setActiveSessionId('sess-a');
    setActiveWorkspaceId('w2');
    const result = await store.resumePendingReplay();
    expect(result.code).toBe('REPLAY_TARGET_CHANGED');
    expect(start).not.toHaveBeenCalled();
  });
});
