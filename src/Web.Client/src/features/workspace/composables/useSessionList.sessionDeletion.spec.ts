import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { SessionMetadata } from '@shared/bridge/hostBridge';

const { bridgeMock, messageMock, supportsMock } = vi.hoisted(() => ({
  bridgeMock: {
    deleteSessionV2: vi.fn(),
    getTimelineSummary: vi.fn(),
    getContextBudget: vi.fn(),
    getSessionMessages: vi.fn(),
    healSessionHistory: vi.fn(),
    getLastActive: vi.fn(),
    setLastActive: vi.fn(),
  },
  messageMock: { success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() },
  supportsMock: vi.fn(() => true),
}));

vi.mock('vue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue')>()),
  onMounted: vi.fn(),
  onUnmounted: vi.fn(),
}));
vi.mock('naive-ui', () => ({
  useMessage: () => messageMock,
  useDialog: () => ({ warning: vi.fn() }),
}));
vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeMock, supportsHostBridgeMethod: supportsMock };
});
vi.mock('@features/agent/stores/useAgentTurn', () => ({
  useAgentTurn: () => ({ runningSessionIds: [] }),
}));
vi.mock('@/stores/askUserStore', () => ({
  useAskUserStore: () => ({ hasPending: () => false, openModal: vi.fn() }),
}));

import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSessionList } from '@/features/workspace/composables/useSessionList';

const WID = 'ws-delete-ui';
const SID = 'sess-delete-ui';

function seedStore() {
  const store = useWorkspaceStore();
  store.activeWorkspaceId = WID;
  store.activeSessionId = SID;
  store.sessions = [{ id: SID, title: '待删除', messageCount: 1 } as SessionMetadata];
  return store;
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.clearAllMocks();
  supportsMock.mockReturnValue(true);
  bridgeMock.getTimelineSummary.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.getContextBudget.mockResolvedValue({ ok: true, data: null });
  bridgeMock.getSessionMessages.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.healSessionHistory.mockResolvedValue({ ok: true, data: { syntheticAppended: 0, orphanToolsRemoved: 0 } });
  bridgeMock.getLastActive.mockResolvedValue({ ok: true, data: { workspaceId: WID, sessionId: SID } });
  bridgeMock.setLastActive.mockResolvedValue({ ok: true, data: true });
});

describe('会话删除交互', () => {
  it('V2 能力缺失时禁用且不发起删除', async () => {
    supportsMock.mockReturnValue(false);
    seedStore();
    const list = useSessionList();

    await list.handleDeleteBtnClick(SID);

    expect(list.canDeleteSession.value).toBe(false);
    expect(list.pendingDeleteSessionId.value).toBeNull();
    expect(bridgeMock.deleteSessionV2).not.toHaveBeenCalled();
    expect(messageMock.warning).toHaveBeenCalledWith('当前宿主不支持安全会话删除，请升级后重试');
  });

  it('双击确认只发起一次删除请求', async () => {
    seedStore();
    let finish!: (value: unknown) => void;
    bridgeMock.deleteSessionV2.mockReturnValue(new Promise((resolve) => (finish = resolve)));
    const list = useSessionList();
    await list.handleDeleteBtnClick(SID);

    const confirming = list.handleDeleteBtnClick(SID);
    const duplicate = list.handleDeleteBtnClick(SID);
    await duplicate;
    expect(bridgeMock.deleteSessionV2).toHaveBeenCalledTimes(1);
    finish({
      ok: true,
      data: {
        outcome: 'deleted',
        destructiveCommitCompleted: true,
        deletedSessionIds: [SID],
        residualSessionIds: [],
        database: 'completed',
        directories: 'completed',
        index: 'completed',
        warnings: [],
      },
    });
    await confirming;
    expect(messageMock.success).toHaveBeenCalledTimes(1);
  });

  it('Partial 使用 warning，不显示统一成功', async () => {
    seedStore();
    bridgeMock.deleteSessionV2.mockResolvedValue({
      ok: true,
      data: {
        outcome: 'partial',
        destructiveCommitCompleted: true,
        deletedSessionIds: [SID],
        residualSessionIds: [SID],
        database: 'completed',
        directories: 'failed',
        index: 'completed',
        warnings: ['DIRECTORY_DELETE_FAILED'],
      },
    });
    const list = useSessionList();

    await list.handleDeleteBtnClick(SID);
    await list.handleDeleteBtnClick(SID);

    expect(messageMock.warning).toHaveBeenCalledWith('会话数据已删除，但 1 个目录或索引项残留，请查看日志/重试');
    expect(messageMock.success).not.toHaveBeenCalled();
  });
});
