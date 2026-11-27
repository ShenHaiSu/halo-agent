import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { setHistoryCapabilitiesState } from '@shared/bridge/hostBridge';
import { useTimelineRowActions } from './useTimelineRowActions';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    userContent: '用户全文',
    userPreview: '用户预览',
    finalBlockId: 'b-final',
    finalPreview: '正文预览',
    userMessageId: 'u1',
    assistantMessageId: 'a2',
    ...over,
  } as TurnSummary;
}

function makeMenu() {
  const targets: Array<Record<string, unknown>> = [];
  return {
    targets,
    openTurnMenu: vi.fn((_: MouseEvent, target: Record<string, unknown>) => targets.push(target)),
    openMenu: vi.fn((_: MouseEvent, target: Record<string, unknown>) => targets.push(target)),
  };
}

const supported = {
  historyAnchors: true,
  replayImages: true,
  atomicHistoryMutex: true,
  strictBranchRollback: true,
  turnDelete: true,
};

describe('历史行菜单只消费真实消息锚点', () => {
  beforeEach(() => {
    setHistoryCapabilitiesState(supported);
  });

  it('用户菜单 id 为 userMessageId 且草稿为原文', () => {
    const menu = makeMenu();
    const row = useTimelineRowActions({
      menu: menu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary(),
    });
    row.onUserMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(menu.targets[0]?.id).toBe('u1');
    expect(menu.targets[0]?.draftText).toBe('用户全文');
    expect(menu.targets[0]?.canReplay).toBe(true);
    expect(menu.targets[0]).not.toHaveProperty('turnIds');
    expect(menu.targets[0]).not.toHaveProperty('turnLabel');
  });

  it('助手菜单 id 为 assistantMessageId，正文仍按 finalBlockId 拉取', async () => {
    const menu = makeMenu();
    const getBlockMarkdown = vi.fn().mockResolvedValue('# 正文全文');
    const row = useTimelineRowActions({
      menu: menu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary(),
      getBlockMarkdown,
    });
    await row.onAssistantMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(menu.targets[0]?.id).toBe('a2');
    expect(getBlockMarkdown).toHaveBeenCalledWith('b-final');
    expect(menu.targets[0]?.draftText).toBe('');
    expect(menu.targets[0]).not.toHaveProperty('turnIds');
    expect(menu.targets[0]).not.toHaveProperty('turnLabel');
  });

  it('缺少用户锚点时禁用破坏性操作但保留复制', () => {
    const menu = makeMenu();
    const row = useTimelineRowActions({
      menu: menu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary({ userMessageId: null }),
    });
    row.onUserMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(menu.targets[0]?.canReplay).toBe(false);
    expect(menu.targets[0]?.canBranch).toBe(false);
    expect(menu.targets[0]?.actionDisabledReason).toContain('用户消息锚点');
    expect(menu.targets[0]?.text).toBe('用户全文');
  });

  it('助手锚点缺失与压缩分隔行分别给出禁用原因', async () => {
    const menu = makeMenu();
    const row = useTimelineRowActions({
      menu: menu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary({ assistantMessageId: null }),
    });
    await row.onAssistantMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(menu.targets[0]?.actionDisabledReason).toContain('可重放的助手消息');

    const compactMenu = makeMenu();
    const compactRow = useTimelineRowActions({
      menu: compactMenu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary({ kind: 'compact' }),
    });
    compactRow.onUserMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(compactMenu.targets[0]?.actionDisabledReason).toContain('压缩分隔行');
  });

  it('能力不足时仅禁用历史操作并提示升级', () => {
    setHistoryCapabilitiesState({ ...supported, historyAnchors: false });
    const menu = makeMenu();
    const row = useTimelineRowActions({
      menu: menu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary(),
    });
    row.onUserMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(menu.targets[0]?.canReplay).toBe(false);
    expect(menu.targets[0]?.actionDisabledReason).toContain('升级');
    expect(menu.targets[0]?.text).toBe('用户全文');
  });
});
