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
    getSessionMessages: vi.fn(),
    getSessions: vi.fn(),
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
    bridgeMock.getSessionMessages.mockResolvedValue({ ok: true, data: [] });
    bridgeMock.getSessions.mockResolvedValue({ ok: true, data: [] });
    bridgeMock.healSessionHistory.mockResolvedValue({ ok: true, data: { syntheticAppended: 0, orphanToolsRemoved: 0 } });
    bridgeMock.getLastActive.mockResolvedValue({ ok: true, data: { workspaceId: null, sessionId: null } });
    bridgeMock.setLastActive.mockResolvedValue({ ok: true, data: true });
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
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', '帮我写一个排序算法'), msg('assistant', '好的，这是实现代码')],
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
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', userText), msg('assistant', assistantText)],
    });
    ws.sessionMessages = [msg('user', userText), msg('assistant', assistantText)];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.updateSessionTitle).toHaveBeenCalledTimes(1);
    expect(bridgeMock.updateSessionTitle.mock.calls[0][2]).toBe(refineTitleLocal(assistantText, userText));
    expect(ws.autoTitleDone[SID]).toBe(true);
  });

  it('generateTitleAfterFirstRound：后端返回真实 LLM 标题时正常回写并置锁', async () => {
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '标题：快速排序算法实现' });
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', '帮我写一个排序算法'), msg('assistant', '好的')],
    });
    const ws = seed('帮我写一');
    ws.sessionMessages = [msg('user', '帮我写一个排序算法'), msg('assistant', '好的')];
    await ws.generateTitleAfterFirstRound(SID);
    expect(ws.sessions[0].title).toBe(parseTitleResult('标题：快速排序算法实现', '帮我写一'));
    expect(ws.titleLocked[SID]).toBe(true);
    expect(ws.autoTitleDone[SID]).toBe(true);
    expect(bridgeMock.updateSessionTitle).not.toHaveBeenCalled();
  });

  it('generateTitleAfterFirstRound：内存被清空但后端有一条 user 时仍正常精炼', async () => {
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '标题：快速排序算法实现' });
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', '帮我写一个排序算法'), msg('assistant', '好的，这是实现代码')],
    });
    const ws = seed('帮我写一');
    ws.sessionMessages = [];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.generateSessionTitle).toHaveBeenCalledTimes(1);
    expect(ws.sessions[0].title).toBe(parseTitleResult('标题：快速排序算法实现', '帮我写一'));
    expect(ws.autoTitleDone[SID]).toBe(true);
  });

  it('generateTitleAfterFirstRound：内存与后端都没有 user 时跳过且不冻结', async () => {
    bridgeMock.getSessionMessages.mockResolvedValue({ ok: true, data: [] });
    const ws = seed('帮我写一');
    ws.sessionMessages = [];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.generateSessionTitle).not.toHaveBeenCalled();
    expect(bridgeMock.updateSessionTitle).not.toHaveBeenCalled();
    expect(ws.autoTitleDone[SID]).toBeUndefined();
    expect(ws.sessions[0].title).toBe('帮我写一');
  });

  it('generateTitleAfterFirstRound：后端多轮时照旧冻结不再请求', async () => {
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', '第一问'), msg('assistant', '答一'), { ...msg('user', '第二问'), id: 'user-2' }],
    });
    const ws = seed('第一问');
    ws.sessionMessages = [];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.generateSessionTitle).not.toHaveBeenCalled();
    expect(ws.autoTitleDone[SID]).toBe(true);
  });

  it('generateTitleAfterFirstRound：后端读取失败时回退内存计数', async () => {
    bridgeMock.getSessionMessages.mockResolvedValue({ ok: false, message: '读失败' });
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '标题：快速排序算法实现' });
    const ws = seed('帮我写一');
    ws.sessionMessages = [msg('user', '帮我写一个排序算法'), msg('assistant', '好的')];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.generateSessionTitle).toHaveBeenCalledTimes(1);
    expect(ws.autoTitleDone[SID]).toBe(true);
  });

  it('generateTitleAfterFirstRound：子会话直接跳过不请求', async () => {
    const ws = seed('帮我写一');
    (ws.sessions[0] as unknown as { agentRole: string }).agentRole = 'sub';
    ws.sessionMessages = [msg('user', '帮我写一个排序算法'), msg('assistant', '好的')];
    await ws.generateTitleAfterFirstRound(SID);
    expect(bridgeMock.getSessionMessages).not.toHaveBeenCalled();
    expect(bridgeMock.generateSessionTitle).not.toHaveBeenCalled();
    expect(ws.autoTitleDone[SID]).toBeUndefined();
  });

  it('selectSession：进入仍是即时标题的会话时补跑一次精炼', async () => {
    const userText = '帮我写一个排序算法';
    const t1 = instantTitleFromUserText(userText);
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', userText), msg('assistant', '好的，这是实现代码')],
    });
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '标题：快速排序算法实现' });
    const ws = useWorkspaceStore();
    ws.activeWorkspaceId = WID;
    ws.activeSessionId = '';
    ws.sessions = [{ id: SID, title: t1, messageCount: 2, createdAtUtc: '', updatedAtUtc: '' } as unknown as SessionMetadata];
    ws.sessionMessages = [];
    ws.titleLocked = {};
    ws.autoTitleDone = {};
    ws.titleGenerating = {};
    await ws.selectSession(SID);
    await vi.waitFor(() => {
      expect(bridgeMock.generateSessionTitle).toHaveBeenCalledTimes(1);
    });
    await vi.waitFor(() => {
      expect(ws.autoTitleDone[SID]).toBe(true);
    });
  });

  it('selectSession：已冻结或多轮会话不补跑', async () => {
    const ws = seed('用户手改标题');
    ws.autoTitleDone = { [SID]: true };
    ws.sessionMessages = [msg('user', '第一问'), msg('assistant', '答一')];
    await ws.selectSession(SID);
    expect(bridgeMock.generateSessionTitle).not.toHaveBeenCalled();
  });

  it('regenerateTitleManually：手动以 force 覆盖已锁标题', async () => {
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', '帮我写一个排序算法'), msg('assistant', '好的，这是实现代码')],
    });
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '标题：快速排序算法实现' });
    const ws = seed('用户手改标题');
    ws.titleLocked = { [SID]: true };
    ws.autoTitleDone = { [SID]: true };
    const ok = await ws.regenerateTitleManually(SID);
    expect(ok).toBe(true);
    expect(bridgeMock.generateSessionTitle).toHaveBeenCalledTimes(1);
    expect(bridgeMock.generateSessionTitle.mock.calls[0][4]).toBe(true);
    expect(ws.sessions[0].title).toBe(parseTitleResult('标题：快速排序算法实现', '帮我写一'));
    expect(ws.titleGenerating[SID]).toBe(false);
  });

  it('regenerateTitleManually：生成中去重，空会话与子会话拒绝', async () => {
    const ws = seed('帮我写一');
    ws.titleGenerating = { [SID]: true };
    expect(await ws.regenerateTitleManually(SID)).toBe(false);
    expect(bridgeMock.generateSessionTitle).not.toHaveBeenCalled();
    ws.titleGenerating = {};
    (ws.sessions[0] as unknown as { agentRole: string }).agentRole = 'sub';
    expect(await ws.regenerateTitleManually(SID)).toBe(false);
    expect(bridgeMock.generateSessionTitle).not.toHaveBeenCalled();
    (ws.sessions[0] as unknown as { agentRole: string }).agentRole = 'main';
    bridgeMock.getSessionMessages.mockResolvedValue({ ok: true, data: [] });
    expect(await ws.regenerateTitleManually(SID)).toBe(false);
    expect(bridgeMock.generateSessionTitle).not.toHaveBeenCalled();
  });

  it('regenerateTitleManually：后端失败走本地兜底', async () => {
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: true,
      data: [msg('user', '帮我写一个排序算法'), msg('assistant', '好的，这是快速排序实现')],
    });
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: false, code: 'TITLE_LLM_ERROR', message: '网络错误' });
    const ws = seed('帮我写一');
    const ok = await ws.regenerateTitleManually(SID);
    expect(ok).toBe(true);
    expect(bridgeMock.updateSessionTitle).toHaveBeenCalledTimes(1);
    expect(ws.titleGenerating[SID]).toBe(false);
  });
  it('regenerateTitleManually：v2 会话 getSessionMessages 拒服时走 getFirstRoundTexts 直读', async () => {
    bridgeMock.getSessionMessages.mockResolvedValue({
      ok: false,
      code: 'USE_TIMELINE_API',
      message: '请使用 getTimelineSummary/getBlockContents',
    });
    (bridgeMock as unknown as Record<string, unknown>).getFirstRoundTexts = vi.fn().mockResolvedValue({
      ok: true,
      data: { firstUser: '帮我写一个排序算法', assistant: '好的，这是实现代码', userCount: 1 },
    });
    bridgeMock.generateSessionTitle.mockResolvedValue({ ok: true, data: '标题：快速排序算法实现' });
    const ws = seed('帮我写一');
    ws.sessionMessages = [];
    try {
      const ok = await ws.regenerateTitleManually(SID);
      expect(ok).toBe(true);
      expect(bridgeMock.generateSessionTitle).toHaveBeenCalledTimes(1);
      expect(ws.sessions[0].title).toBe(parseTitleResult('标题：快速排序算法实现', '帮我写一'));
      expect(ws.titleGenerating[SID]).toBe(false);
    } finally {
      delete (bridgeMock as unknown as Record<string, unknown>).getFirstRoundTexts;
    }
  });
});
