import { beforeEach, describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { setHistoryCapabilitiesState } from '@shared/bridge/hostBridge';

const { bridgeMock, messageMock, dialogMock, streamRefs, turnMock } = vi.hoisted(() => ({
  bridgeMock: { deleteTurn: vi.fn(), deleteMessage: vi.fn() },
  messageMock: { success: vi.fn(), error: vi.fn(), warning: vi.fn() },
  dialogMock: { warning: vi.fn() },
  streamRefs: { generating: null as unknown, streaming: null as unknown },
  turnMock: { isCompacting: vi.fn(() => false) },
}));

function generating(): { value: boolean } {
  return streamRefs.generating as { value: boolean };
}

vi.mock('naive-ui', () => ({ useDialog: () => dialogMock, useMessage: () => messageMock }));
vi.mock('@/stores/workspaceStore', () => ({ useWorkspaceStore: () => bridgeMock }));
vi.mock('@features/agent/stores/useAgentStream', async () => {
  const { ref } = await import('vue');
  streamRefs.generating = ref(false);
  streamRefs.streaming = ref(false);
  return {
    useAgentStream: () => ({
      get isGenerating() {
        return (streamRefs.generating as { value: boolean }).value;
      },
      get streamingActive() {
        return (streamRefs.streaming as { value: boolean }).value;
      },
    }),
  };
});
vi.mock('@features/agent/stores/useAgentTurn', () => ({ useAgentTurn: () => turnMock }));

import { useTimelineMenu } from './useTimelineMenu';

const capabilities = {
  historyAnchors: true,
  replayImages: true,
  atomicHistoryMutex: true,
  strictBranchRollback: true,
  turnDelete: true,
};

type ConfirmOptions = {
  title: string;
  content: string;
  positiveText: string;
  onPositiveClick: () => Promise<void>;
};

function confirmOf(options?: ConfirmOptions | null): ConfirmOptions {
  if (!options) throw new Error('未弹出删除确认框');
  return options;
}

function openDeleteDialog(targetId = 'msg-user-a') {
  const menu = useTimelineMenu();
  menu.openMenu({ clientX: 32, clientY: 48 } as MouseEvent, {
    kind: 'user',
    id: targetId,
    text: '提问原文',
    turnText: '整轮原文',
  });
  menu.onMenuDelete();
  const options = dialogMock.warning.mock.calls[0]?.[0] as ConfirmOptions | undefined;
  return { menu, options };
}

beforeEach(() => {
  vi.clearAllMocks();
  generating().value = false;
  (streamRefs.streaming as { value: boolean }).value = false;
  turnMock.isCompacting.mockReturnValue(false);
  bridgeMock.deleteTurn.mockResolvedValue({ ok: true });
  setHistoryCapabilitiesState(capabilities);
});

describe('右键删除本轮', () => {
  it('确认文案与按钮只表达整轮语义，且只调用一次 anchor 删除', async () => {
    const { options } = openDeleteDialog();
    const confirm = confirmOf(options);
    expect(confirm.title).toBe('删除本轮');
    expect(confirm.positiveText).toBe('删除本轮');
    expect(confirm.content).toContain('该轮提问、回复及工具记录');
    expect(confirm.content).not.toContain('只删一条');

    await confirm.onPositiveClick();
    expect(bridgeMock.deleteTurn).toHaveBeenCalledTimes(1);
    expect(bridgeMock.deleteTurn).toHaveBeenCalledWith('msg-user-a');
    expect(bridgeMock.deleteMessage).not.toHaveBeenCalled();
    expect(messageMock.success).toHaveBeenCalledWith('本轮已删除');
  });

  it('弹窗停留期间进入忙态，确认时不发请求', async () => {
    const confirm = confirmOf(openDeleteDialog().options);
    generating().value = true;
    await confirm.onPositiveClick();
    expect(bridgeMock.deleteTurn).not.toHaveBeenCalled();
  });

  it('双击确认只提交一次', async () => {
    let complete: (value: { ok: boolean }) => void = () => undefined;
    bridgeMock.deleteTurn.mockImplementation(() => new Promise<{ ok: boolean }>((resolve) => (complete = resolve)));
    const confirm = confirmOf(openDeleteDialog().options);

    const first = confirm.onPositiveClick();
    const second = confirm.onPositiveClick();
    expect(bridgeMock.deleteTurn).toHaveBeenCalledTimes(1);
    complete({ ok: true });
    await Promise.all([first, second]);
    expect(bridgeMock.deleteTurn).toHaveBeenCalledTimes(1);
  });

  it('失败与元数据告警分别提示，不伪造成功', async () => {
    bridgeMock.deleteTurn.mockResolvedValueOnce({ ok: false, message: '备份失败' });
    await confirmOf(openDeleteDialog('msg-user-b').options).onPositiveClick();
    expect(messageMock.error).toHaveBeenCalledWith('备份失败');
    expect(messageMock.success).not.toHaveBeenCalled();

    bridgeMock.deleteTurn.mockResolvedValueOnce({ ok: true, metadataWarning: 'METADATA_REFRESH_FAILED' });
    await confirmOf(openDeleteDialog('msg-user-c').options).onPositiveClick();
    expect(messageMock.warning).toHaveBeenCalledWith('本轮已删除，但元数据刷新失败，请刷新确认');
  });

  it('宿主缺少整轮删除能力时不弹确认、不发请求', () => {
    setHistoryCapabilitiesState({ ...capabilities, turnDelete: false });
    const { menu, options } = openDeleteDialog();
    expect(menu.menuCanDelete.value).toBe(false);
    expect(options).toBeUndefined();
    expect(dialogMock.warning).not.toHaveBeenCalled();
    expect(bridgeMock.deleteTurn).not.toHaveBeenCalled();
  });

  it('本模块不再直写 window.$message', () => {
    const source = readFileSync(resolve(__dirname, './useTimelineMenu.ts'), 'utf8');
    expect(source).not.toContain('$message');
  });
});
