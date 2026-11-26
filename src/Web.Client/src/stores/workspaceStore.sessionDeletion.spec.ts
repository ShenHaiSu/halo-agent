import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { SessionDeletionResultDto, SessionMetadata, UnifiedChatMessage } from '@shared/bridge/hostBridge';

const { bridgeMock } = vi.hoisted(() => ({
  bridgeMock: {
    deleteSessionV2: vi.fn(),
    getTimelineSummary: vi.fn(),
    getContextBudget: vi.fn(),
    getSessionMessages: vi.fn(),
    healSessionHistory: vi.fn(),
    getLastActive: vi.fn(),
    setLastActive: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeMock };
});

import { useWorkspaceStore } from '@/stores/workspaceStore';

const WID = 'ws-delete-spec';
const SID = 'sess-delete-root';
const OTHER_SID = 'sess-delete-other';
const CHILD_SID = 'sess-delete-child';

function session(id: string): SessionMetadata {
  return {
    id,
    workspaceId: WID,
    title: id,
    messageCount: 1,
    createdAtUtc: '2026-01-01T00:00:00Z',
    updatedAtUtc: '2026-01-01T00:00:00Z',
  } as SessionMetadata;
}

function deletionResult(overrides: Partial<SessionDeletionResultDto> = {}): SessionDeletionResultDto {
  return {
    outcome: 'deleted',
    destructiveCommitCompleted: true,
    backupFileName: 'history.db.bak',
    deletedSessionIds: [SID],
    residualSessionIds: [],
    database: 'completed',
    directories: 'completed',
    index: 'completed',
    warnings: [],
    ...overrides,
  };
}

function seedStore(ids = [SID, OTHER_SID]) {
  const store = useWorkspaceStore();
  store.activeWorkspaceId = WID;
  store.activeSessionId = SID;
  store.sessions = ids.map(session);
  store.sessionMessages = [{ id: 'msg-delete', role: 'user', content: 'hello' } as UnifiedChatMessage];
  store.timelineSummaries = [];
  store.budgetStatus = null;
  return store;
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.clearAllMocks();
  bridgeMock.getTimelineSummary.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.getContextBudget.mockResolvedValue({ ok: true, data: null });
  bridgeMock.getSessionMessages.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.healSessionHistory.mockResolvedValue({ ok: true, data: { syntheticAppended: 0, orphanToolsRemoved: 0 } });
  bridgeMock.getLastActive.mockResolvedValue({ ok: true, data: { workspaceId: WID, sessionId: SID } });
  bridgeMock.setLastActive.mockResolvedValue({ ok: true, data: true });
});

describe('workspaceStore 安全会话删除', () => {
  it('Failed 不移除本地项、不切换 active', async () => {
    const store = seedStore();
    bridgeMock.deleteSessionV2.mockResolvedValue({
      ok: true,
      data: deletionResult({ outcome: 'failed', destructiveCommitCompleted: false, deletedSessionIds: [] }),
    });

    const result = await store.deleteSession(SID);

    expect(result.outcome).toBe('failed');
    expect(store.sessions.map((item) => item.id)).toEqual([SID, OTHER_SID]);
    expect(store.activeSessionId).toBe(SID);
  });

  it('Partial 保留 residual 项并移除已提交且无残留的项', async () => {
    const store = seedStore([SID, CHILD_SID, OTHER_SID]);
    bridgeMock.deleteSessionV2.mockResolvedValue({
      ok: true,
      data: deletionResult({
        outcome: 'partial',
        errorCode: 'SESSION_DELETION_PARTIAL',
        deletedSessionIds: [SID, CHILD_SID],
        residualSessionIds: [SID],
        directories: 'failed',
      }),
    });

    const result = await store.deleteSession(SID);

    expect(result.outcome).toBe('partial');
    expect(store.sessions.map((item) => item.id)).toEqual([SID, OTHER_SID]);
    expect(store.activeSessionId).toBe(SID);
  });

  it('删除当前会话后切换到剩余第一个会话', async () => {
    const store = seedStore();
    bridgeMock.deleteSessionV2.mockResolvedValue({ ok: true, data: deletionResult() });

    await store.deleteSession(SID);

    expect(store.sessions.map((item) => item.id)).toEqual([OTHER_SID]);
    expect(store.activeSessionId).toBe(OTHER_SID);
  });

  it('删除最后一个会话后清空消息、摘要、预算与缓存', async () => {
    const store = seedStore([SID]);
    store.blockContentsCache = { block: { blockId: 'block' } as never };
    bridgeMock.deleteSessionV2.mockResolvedValue({ ok: true, data: deletionResult() });

    await store.deleteSession(SID);

    expect(store.sessions).toEqual([]);
    expect(store.activeSessionId).toBe('');
    expect(store.sessionMessages).toEqual([]);
    expect(store.timelineSummaries).toEqual([]);
    expect(store.budgetStatus).toBeNull();
    expect(store.blockContentsCache).toEqual({});
  });

  it('能力缺失的失败响应不会修改本地状态', async () => {
    const store = seedStore();
    bridgeMock.deleteSessionV2.mockResolvedValue({
      ok: false,
      code: 'SESSION_DELETE_UNSUPPORTED',
      message: '请升级宿主',
    });

    const result = await store.deleteSession(SID);

    expect(result).toMatchObject({ outcome: 'failed', errorCode: 'SESSION_DELETE_UNSUPPORTED' });
    expect(store.sessions.map((item) => item.id)).toEqual([SID, OTHER_SID]);
    expect(store.activeSessionId).toBe(SID);
  });
});
