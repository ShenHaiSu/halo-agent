import { describe, expect, it, vi } from 'vitest';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { useTimelineRowActions } from './useTimelineRowActions';

function makeSummary(over: Partial<TurnSummary> = {}): TurnSummary {
  return {
    turnId: 'uturn-u1',
    userContent: '用户全文',
    userPreview: '用户预览',
    finalBlockId: 'b-final',
    finalPreview: '正文预览',
    ...over,
  } as TurnSummary;
}

function makeMenu() {
  const calls: unknown[] = [];
  return {
    calls,
    openTurnMenu: vi.fn((_: MouseEvent, target: unknown) => {
      calls.push(target);
    }),
    openMenu: vi.fn((_: MouseEvent, target: unknown) => {
      calls.push(target);
    }),
  };
}

describe('useTimelineRowActions B 方案口径', () => {
  it('用户右键：text 仅用户，turnText 为整轮', () => {
    const menu = makeMenu();
    const { onUserMenu } = useTimelineRowActions({
      menu: menu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary(),
    });
    onUserMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(menu.openTurnMenu).toHaveBeenCalledTimes(1);
    const target = (menu.openTurnMenu.mock.calls[0] as unknown[])[1] as {
      text: string;
      turnText: string;
      copyLabel: string;
    };
    expect(target.text).toBe('用户全文');
    expect(target.turnText).toContain('用户全文');
    expect(target.turnText).toContain('正文预览');
    expect(target.copyLabel).toBe('复制此提问');
  });

  it('助手右键：text 仅正文块，turnText 为问+答', async () => {
    const menu = makeMenu();
    const { onAssistantMenu } = useTimelineRowActions({
      menu: menu as never,
      retryTurn: () => undefined,
      findSummary: () => makeSummary(),
      getBlockMarkdown: async () => '# 标题\n\n正文块全文',
    });
    await onAssistantMenu({} as MouseEvent, { key: 'live-uturn-u1' } as never);
    expect(menu.openTurnMenu).toHaveBeenCalledTimes(1);
    const target = (menu.openTurnMenu.mock.calls[0] as unknown[])[1] as {
      text: string;
      turnText: string;
      copyLabel: string;
    };
    expect(target.text).toBe('# 标题\n\n正文块全文');
    expect(target.turnText).toContain('用户全文');
    expect(target.turnText).toContain('正文块全文');
    expect(target.copyLabel).toBe('复制此回复');
  });
});
