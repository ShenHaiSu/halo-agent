import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { bridgeMock } = vi.hoisted(() => ({
  bridgeMock: {
    branchSession: vi.fn(),
    getTimelineSummary: vi.fn(),
    healSessionHistory: vi.fn(),
    getContextBudget: vi.fn(),
    getLastActive: vi.fn(),
    setLastActive: vi.fn(),
    getSessions: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeMock };
});

import { useWorkspaceStore } from '@/stores/workspaceStore';
import { haloEventBus } from '@shared/bridge/hostBridge';

const WID = 'ws-branch-spec';
const SID = 'sess-branch-source';
const NEW_SID = 'sess-branch-new';

function branchSession(id: string) {
  return { id, workspaceId: WID, title: '分支会话', messageCount: 0 } as never;
}

beforeEach(() => {
  setActivePinia(createPinia());
  bridgeMock.branchSession.mockReset();
  bridgeMock.getTimelineSummary.mockReset();
  bridgeMock.healSessionHistory.mockReset();
  bridgeMock.getContextBudget.mockReset();
  bridgeMock.getLastActive.mockReset();
  bridgeMock.setLastActive.mockReset();
  bridgeMock.getSessions.mockReset();
  bridgeMock.getTimelineSummary.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.healSessionHistory.mockResolvedValue({ ok: true, data: { success: true } });
  bridgeMock.getContextBudget.mockResolvedValue({ ok: true, data: null });
  bridgeMock.getLastActive.mockResolvedValue({ ok: false });
  bridgeMock.setLastActive.mockResolvedValue({ ok: true, data: true });
  bridgeMock.getSessions.mockResolvedValue({ ok: true, data: [] });
});

function seedStore() {
  const store = useWorkspaceStore();
  store.activeWorkspaceId = WID;
  store.activeSessionId = SID;
  store.sessionMessages = [];
  return store;
}

describe('历史分支草稿与结构化结果', () => {
  it('用户分支从目标文本预填草稿，即使全量消息数组为空', async () => {
    bridgeMock.branchSession.mockResolvedValue({ ok: true, data: branchSession(NEW_SID) });
    const store = seedStore();
    const prefill = vi.fn();
    const unsubscribe = haloEventBus.on('chat:prefill-draft', prefill);
    const result = await store.branchFromMessage('u1', 'user', '第一轮提问原文');
    expect(result.ok).toBe(true);
    expect(result.switched).toBe(true);
    expect(prefill).toHaveBeenCalled();
    const payload = prefill.mock.calls[0]?.[0] as { text: string; sessionId: string };
    expect(payload.text).toBe('第一轮提问原文');
    expect(payload.sessionId).toBe(NEW_SID);
    unsubscribe();
  });

  it('助手分支草稿为空并包含整轮', async () => {
    bridgeMock.branchSession.mockResolvedValue({ ok: true, data: branchSession(NEW_SID) });
    const store = seedStore();
    const result = await store.branchFromMessage('a2', 'assistant', '不该被使用');
    expect(result.ok).toBe(true);
    expect(bridgeMock.branchSession.mock.calls[0]?.[3]).toBe(true);
  });

  it('分支失败返回稳定错误码且不切换会话', async () => {
    bridgeMock.branchSession.mockResolvedValue({
      ok: false,
      code: 'ANCHOR_NOT_FOUND',
      message: '历史已变化',
    });
    const store = seedStore();
    const result = await store.branchFromMessage('missing', 'user', '原文');
    expect(result.ok).toBe(false);
    expect(result.code).toBe('ANCHOR_NOT_FOUND');
    expect(store.activeSessionId).toBe(SID);
  });

  it('回滚失败携带残留会话 ID 与失败阶段', async () => {
    bridgeMock.branchSession.mockResolvedValue({
      ok: false,
      code: 'BRANCH_ROLLBACK_FAILED',
      message: '清理失败',
      newSessionId: 'sess_residual',
      failedStage: 'Directory',
    });
    const store = seedStore();
    const result = await store.branchFromMessage('u1', 'user', '原文');
    expect(result.code).toBe('BRANCH_ROLLBACK_FAILED');
    expect(result.newSessionId).toBe('sess_residual');
    expect(result.failedStage).toBe('Directory');
  });

  it('分支期间切换会话只创建不抢切', async () => {
    bridgeMock.branchSession.mockImplementation(async () => {
      storeForSwitch.activeSessionId = 'sess-other';
      return { ok: true, data: branchSession(NEW_SID) };
    });
    const storeForSwitch = seedStore();
    const result = await storeForSwitch.branchFromMessage('u1', 'user', '原文');
    expect(result.ok).toBe(true);
    expect(result.switched).toBe(false);
    expect(storeForSwitch.activeSessionId).toBe('sess-other');
  });
});
