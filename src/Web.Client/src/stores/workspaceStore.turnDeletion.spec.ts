import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { bridgeMock, pendingMock } = vi.hoisted(() => ({
  bridgeMock: {
    deleteSessionTurn: vi.fn(),
    deleteSessionMessage: vi.fn(),
    getTimelineSummary: vi.fn(),
    getContextBudget: vi.fn(),
    getSessionMessages: vi.fn(),
  },
  pendingMock: { dropPendingUser: vi.fn() },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeMock };
});
vi.mock('@features/agent/stores/model', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@features/agent/stores/model')>();
  return { ...actual, dropPendingUser: pendingMock.dropPendingUser };
});

import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSessionStore } from '@/stores/sessionStore';

const workspaceId = 'ws-turn-delete';
const sessionId = 'sess-turn-target';
const otherSessionId = 'sess-turn-other';
const anchorId = 'msg-turn-anchor';

function successResult(overrides: Record<string, unknown> = {}) {
  return {
    ok: true,
    data: {
      success: true,
      deletedTurnId: 'uturn-msg-turn-user',
      deletedMessages: 2,
      keptMessages: 3,
      syntheticAppended: 0,
      orphanToolsRemoved: 0,
      backupFileName: 'history.db.bak',
      destructiveCommitCompleted: true,
      ...overrides,
    },
  };
}

function seedStore() {
  const store = useWorkspaceStore();
  store.activeWorkspaceId = workspaceId;
  store.activeSessionId = sessionId;
  return store;
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.clearAllMocks();
  bridgeMock.getTimelineSummary.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.getContextBudget.mockResolvedValue({ ok: true, data: null });
  bridgeMock.getSessionMessages.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.deleteSessionTurn.mockResolvedValue(successResult());
});

describe('workspaceStore 整轮删除', () => {
  it('成功后只清目标会话 pending，并重载当前视图与预算', async () => {
    const store = seedStore();

    const result = await store.deleteTurn(anchorId);

    expect(result.ok).toBe(true);
    expect(bridgeMock.deleteSessionTurn).toHaveBeenCalledWith(workspaceId, sessionId, anchorId);
    expect(pendingMock.dropPendingUser).toHaveBeenCalledWith(sessionId);
    expect(bridgeMock.getTimelineSummary).toHaveBeenCalledWith(workspaceId, sessionId);
    expect(bridgeMock.getContextBudget).toHaveBeenCalled();
    expect(bridgeMock.deleteSessionMessage).not.toHaveBeenCalled();
  });

  it('失败时不提前清 pending，也不重载视图造成假成功', async () => {
    const store = seedStore();
    bridgeMock.deleteSessionTurn.mockResolvedValue({
      ok: false,
      code: 'BACKUP_FAILED',
      message: '备份失败',
    });

    const result = await store.deleteTurn(anchorId);

    expect(result.ok).toBe(false);
    expect(result.code).toBe('BACKUP_FAILED');
    expect(pendingMock.dropPendingUser).not.toHaveBeenCalled();
    expect(bridgeMock.getTimelineSummary).not.toHaveBeenCalled();
    expect(bridgeMock.getContextBudget).not.toHaveBeenCalled();
  });

  it('提交未完成时不视为成功，清 pending 与刷新都不发生', async () => {
    seedStore();
    bridgeMock.deleteSessionTurn.mockResolvedValue({
      ok: true,
      data: { success: false, destructiveCommitCompleted: false },
    });

    const result = await useWorkspaceStore().deleteTurn(anchorId);

    expect(result.ok).toBe(false);
    expect(pendingMock.dropPendingUser).not.toHaveBeenCalled();
    expect(bridgeMock.getTimelineSummary).not.toHaveBeenCalled();
  });

  it('请求中切会话：旧响应只清目标 pending，不刷新新会话', async () => {
    const store = seedStore();
    let complete!: (value: unknown) => void;
    bridgeMock.deleteSessionTurn.mockImplementation(() => new Promise((resolve) => (complete = resolve)));

    const request = store.deleteTurn(anchorId);
    useSessionStore().beginSelect(otherSessionId);
    store.activeSessionId = otherSessionId;
    complete(successResult());
    const result = await request;

    expect(result.ok).toBe(true);
    expect(pendingMock.dropPendingUser).toHaveBeenCalledWith(sessionId);
    expect(bridgeMock.getTimelineSummary).not.toHaveBeenCalled();
    expect(bridgeMock.getContextBudget).not.toHaveBeenCalled();
    expect(useSessionStore().staleDiscarded).toBe(1);
  });

  it('已提交但元数据告警时原样返回告警给上层', async () => {
    seedStore();
    bridgeMock.deleteSessionTurn.mockResolvedValue(successResult({ metadataWarning: 'METADATA_REFRESH_FAILED' }));

    const result = await useWorkspaceStore().deleteTurn(anchorId);

    expect(result.ok).toBe(true);
    expect(result.metadataWarning).toBe('METADATA_REFRESH_FAILED');
  });
});
