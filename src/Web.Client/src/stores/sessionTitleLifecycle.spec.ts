/** 会话标题两阶段状态门闩回归：即时标题不置锁、冻结信号停手、兜底与正向精炼。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { instantTitleFromUserText, parseTitleResult, refineTitleLocal } from '@shared/utils/sessionTitle';
import type { SessionMetadata, UnifiedChatMessage } from '@shared/bridge/hostBridge';

const { bridgeMock } = vi.hoisted(() => ({
  bridgeMock: {
    appendSessionMessage: vi.fn(),
    updateSessionTitle: vi.fn(),
    generateSessionTitle: vi.fn(),
    getContextBudget: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeMock };
});

import { useWorkspaceStore } from '@/stores/workspaceStore';

const WID = 'ws-title-spec';
const SID = 'sess-title-spec';

function msg(role: 'user' | 'assistant', content: string): UnifiedChatMessage {
  return { id: `${role}-1`, role, content, createdAtUtc: '2026-01-01T00:00:00Z' } as unknown as UnifiedChatMessage;
}

function seed(title = '新建会话') {
  const ws = useWorkspaceStore();
  ws.activeWorkspaceId = WID;
  ws.activeSessionId = SID;
  ws.sessions = [{ id: SID, title, messageCount: 0, createdAtUtc: '', updatedAtUtc: '' } as unknown as SessionMetadata];
  ws.sessionMessages = [];
  ws.titleLocked = {};
  ws.autoTitleDone = {};
  ws.titleGenerating = {};
  return ws;
}

describe('会话标题两阶段状态门闩', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    bridgeMock.appendSessionMessage.mockResolvedValue({ ok: true, data: true });
    bridgeMock.updateSessionTitle.mockResolvedValue({ ok: true, data: true });
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '' });
    bridgeMock.getContextBudget.mockResolvedValue({ ok: true, data: null });
  });

  it('appendMessage：首条 user 不再调用改名通道，但即时标题照常更新', async () => {
    const ws = seed();
    const text = '帮我写一个排序算法。后面忽略';
    const ok = await ws.appendMessage(msg('user', text));
    expect(ok).toBe(true);
    expect(bridgeMock.updateSessionTitle).not.toHaveBeenCalled();
    expect(ws.sessions[0].title).toBe(instantTitleFromUserText(text));
  });

  it('requestTitleViaLlm：冻结码使抛出的错误携带 titleCode', async () => {
    bridgeMock.generateSessionTitle.mockResolvedValue({
      ok: false,
      code: 'TITLE_ALREADY_LOCKED',
      message: '标题已冻结',
    });
    const ws = seed();
    await expect(ws.requestTitleViaLlm(SID, 20000)).rejects.toMatchObject({
      titleCode: 'TITLE_ALREADY_LOCKED',
    });
  });

  it('generateTitleAfterFirstRound：冻结码立即停手，不覆盖标题、不改名、autoTitleDone 置位', async () => {
    bridgeMock.generateSessionTitle.mockResolvedValue({
      ok: false,
      code: 'TITLE_ALREADY_LOCKED',
      message: '标题已冻结',
    });
    const ws = seed('用户手改标题');
    ws.sessionMessages = [msg('user', '帮我写一个排序算法'), msg('assistant', '好的，这是实现代码')];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.updateSessionTitle).not.toHaveBeenCalled();
    expect(ws.sessions[0].title).toBe('用户手改标题');
    expect(ws.autoTitleDone[SID]).toBe(true);
  });

  it('generateTitleAfterFirstRound：其他失败仍走本地精炼兜底', async () => {
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: false, code: 'TITLE_LLM_ERROR', message: '网络错误' });
    const ws = seed('帮我写一');
    const userText = '帮我写一个排序算法';
    const assistantText = '好的，这是快速排序的实现代码，包含分区逻辑与递归终止条件';
    ws.sessionMessages = [msg('user', userText), msg('assistant', assistantText)];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.updateSessionTitle).toHaveBeenCalledTimes(1);
    expect(bridgeMock.updateSessionTitle.mock.calls[0][2]).toBe(refineTitleLocal(assistantText, userText));
    expect(ws.autoTitleDone[SID]).toBe(true);
  });

  it('generateTitleAfterFirstRound：后端返回真实 LLM 标题时正常回写并置锁', async () => {
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '标题：快速排序算法实现' });
    const ws = seed('帮我写一');
    ws.sessionMessages = [msg('user', '帮我写一个排序算法'), msg('assistant', '好的')];
    await ws.generateTitleAfterFirstRound(SID);
    expect(ws.sessions[0].title).toBe(parseTitleResult('标题：快速排序算法实现', '帮我写一'));
    expect(ws.titleLocked[SID]).toBe(true);
    expect(ws.autoTitleDone[SID]).toBe(true);
    expect(bridgeMock.updateSessionTitle).not.toHaveBeenCalled();
  });
});
