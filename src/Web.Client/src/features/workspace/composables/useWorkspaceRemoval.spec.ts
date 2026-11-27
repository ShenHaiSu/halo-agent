import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { WorkspaceInfo, WorkspaceRemovalResultDto } from '@shared/bridge/hostBridge';

const { bridgeMock, messageMock, supportsMock } = vi.hoisted(() => ({
  bridgeMock: {
    removeWorkspaceV2: vi.fn(),
    getSessions: vi.fn(),
    setLastActive: vi.fn(),
  },
  messageMock: { success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() },
  supportsMock: vi.fn(() => true),
}));

vi.mock('naive-ui', () => ({ useMessage: () => messageMock }));
vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeMock, supportsHostBridgeMethod: supportsMock };
});

import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useWorkspaceRemoval } from '@/features/workspace/composables/useWorkspaceRemoval';

const firstId = 'ws-removal-active';
const secondId = 'ws-removal-other';
const displayName = '别名优先';

function seedStore() {
  const store = useWorkspaceStore();
  store.workspaces = [
    { id: firstId, name: '文件夹名', alias: displayName, rootPath: 'C:\\projects\\one' } as WorkspaceInfo,
    { id: secondId, name: '另一个', rootPath: 'C:\\projects\\two' } as WorkspaceInfo,
  ];
  store.activeWorkspaceId = firstId;
  return store;
}

function okResult(overrides: Partial<WorkspaceRemovalResultDto> = {}): WorkspaceRemovalResultDto {
  return {
    outcome: 'completed',
    mode: 'unbind',
    errorCode: null,
    workspaceUnbound: true,
    sessionData: 'notRequested',
    haloAgent: 'deleted',
    haloAgentReason: 'EMPTY',
    failedPaths: [],
    warnings: [],
    ...overrides,
  };
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.clearAllMocks();
  supportsMock.mockReturnValue(true);
  bridgeMock.getSessions.mockResolvedValue({ ok: true, data: [] });
  bridgeMock.setLastActive.mockResolvedValue({ ok: true, data: true });
  bridgeMock.removeWorkspaceV2.mockResolvedValue({ ok: true, data: okResult() });
});

describe('工作区双模式交互', () => {
  it('V2 能力缺失时只提示升级，不打开弹窗也不发请求', () => {
    supportsMock.mockReturnValue(false);
    seedStore();
    const removal = useWorkspaceRemoval();

    removal.requestRemoval(firstId);

    expect(removal.canRemoveWorkspace.value).toBe(false);
    expect(removal.dialogOpen.value).toBe(false);
    expect(bridgeMock.removeWorkspaceV2).not.toHaveBeenCalled();
    expect(messageMock.warning).toHaveBeenCalledWith('当前宿主不支持工作区双模式移除，请升级后重试');
  });

  it('最后一个工作区不显示移除入口', () => {
    const store = seedStore();
    store.workspaces = [store.workspaces[0]];
    const removal = useWorkspaceRemoval();

    removal.requestRemoval(firstId);

    expect(removal.dialogOpen.value).toBe(false);
    expect(messageMock.warning).toHaveBeenCalledWith('至少需要保留一个工作区');
  });

  it('Purge 必须完全匹配显示名，名称错误或为空时确认不可用', async () => {
    seedStore();
    const removal = useWorkspaceRemoval();
    removal.requestRemoval(firstId);
    removal.setMode('purge');

    expect(removal.confirmValid.value).toBe(false);
    await removal.confirmRemoval('purge', '');
    expect(bridgeMock.removeWorkspaceV2).not.toHaveBeenCalled();

    await removal.confirmRemoval('purge', '文件夹名');
    expect(bridgeMock.removeWorkspaceV2).not.toHaveBeenCalled();

    await removal.confirmRemoval('purge', displayName);
    expect(bridgeMock.removeWorkspaceV2).toHaveBeenCalledWith(firstId, 'purge');
  });

  it('busy 期间双击只发一次请求，响应到达后立即释放', async () => {
    seedStore();
    let finish!: (value: unknown) => void;
    bridgeMock.removeWorkspaceV2.mockReturnValue(new Promise((resolve) => (finish = resolve)));
    const removal = useWorkspaceRemoval();
    removal.requestRemoval(firstId);

    const first = removal.confirmRemoval();
    const duplicate = removal.confirmRemoval();
    await duplicate;
    expect(bridgeMock.removeWorkspaceV2).toHaveBeenCalledTimes(1);
    expect(removal.busy.value).toBe(true);

    finish({ ok: true, data: okResult() });
    await first;
    expect(removal.busy.value).toBe(false);
    expect(removal.dialogOpen.value).toBe(false);
  });

  it('Failed 保留本地工作区，CompletedWithWarnings 仍移除并给出 warning', async () => {
    const store = seedStore();
    const removal = useWorkspaceRemoval();
    bridgeMock.removeWorkspaceV2.mockResolvedValueOnce({ ok: false, code: 'WORKSPACE_BUSY' });

    removal.requestRemoval(firstId);
    await removal.confirmRemoval();
    expect(store.workspaces).toHaveLength(2);
    expect(messageMock.error).toHaveBeenCalledWith('移除失败，工作区仍在列表中');

    bridgeMock.removeWorkspaceV2.mockResolvedValueOnce({
      ok: true,
      data: okResult({
        outcome: 'completedWithWarnings',
        mode: 'purge',
        failedPaths: ['C:\\project\\.haloagent'],
      }),
    });
    removal.requestRemoval(firstId);
    await removal.confirmRemoval();
    expect(store.workspaces.map((ws) => ws.id)).toEqual([secondId]);
    expect(messageMock.warning).toHaveBeenCalledWith('已移出列表，但部分物理数据清理失败或被保守保留（1 个路径需人工检查）');
  });

  it('响应到达时使用捕获的目标 ID，不使用当前 active 猜测', async () => {
    const store = seedStore();
    const removal = useWorkspaceRemoval();
    removal.requestRemoval(secondId);
    store.activeWorkspaceId = firstId;
    bridgeMock.removeWorkspaceV2.mockResolvedValueOnce({
      ok: true,
      data: okResult({ workspaceUnbound: true }),
    });

    await removal.confirmRemoval();

    expect(bridgeMock.removeWorkspaceV2).toHaveBeenCalledWith(secondId, 'unbind');
    expect(store.workspaces.map((ws) => ws.id)).toEqual([firstId]);
    expect(store.activeWorkspaceId).toBe(firstId);
  });
});
