import { turnCopyText, turnMessageIds, turnReplayAnchor } from '@features/session/utils/userTurn';
import type { UserTurn } from '@features/session/utils/userTurn';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import type { TimelineMenu } from './useTimelineMenu';

interface RowActionDeps {
  menu: TimelineMenu;
  toggleTurn: (key: string) => void;
  toggleRun: (key: string) => void;
  retryTurn: () => void;
}

/**
 * 轮级行行为：展开分流、轮级菜单（复制/重放/分支/删除整轮）、失败重发。
 * 纯行为层，不持有任何渲染状态，装配层调用后只转发事件。
 */
export function useTimelineRowActions(deps: RowActionDeps) {
  /** 合并舱与普通轮共用同一条 toggle 事件，按 key 前缀分流。 */
  function onToggle(key: string): void {
    if (key.startsWith('run-') || key.startsWith('uturn-')) deps.toggleRun(key);
    else deps.toggleTurn(key);
  }

  function turnOf(item: TimelineEntry): UserTurn | null {
    try {
      const u = item as unknown as { kind?: string; turn?: UserTurn };
      if (u && u.kind === 'user-turn' && u.turn) return u.turn;
    } catch (e) {
      console.warn('[timeline] turn resolve failed:', e);
    }
    return null;
  }

  /** 轮级菜单：复制整轮（全文展开），重放/分支/删除按轮锚点。 */
  function onUserMenu(e: MouseEvent, item: TimelineEntry): void {
    const turn = turnOf(item);
    if (turn) {
      const anchor = turnReplayAnchor(turn);
      const ids = turnMessageIds(turn);
      deps.menu.openTurnMenu(e, {
        kind: 'user',
        id: anchor ? anchor.id : String(turn.userId || ''),
        text: turnCopyText(turn),
        turnIds: ids,
        turnLabel: `整轮（${ids.length}条）`,
      });
      return;
    }
    deps.menu.openMenu(e, { kind: 'user', id: String(item.msg?.id || ''), text: String(item.msg?.content || '') });
  }

  /** 助手区右键同样按轮（锚点取本轮末 assistant，重放/分支/删除整轮）。 */
  function onAssistantMenu(e: MouseEvent, item: TimelineEntry): void {
    const turn = turnOf(item);
    if (turn) {
      let assistantId = '';
      for (let i = turn.steps.length - 1; i >= 0; i--) {
        const sid = String(turn.steps[i]?.assistantId || '');
        if (sid) {
          assistantId = sid;
          break;
        }
      }
      const anchor = turnReplayAnchor(turn);
      const ids = turnMessageIds(turn);
      deps.menu.openTurnMenu(e, {
        kind: 'assistant',
        id: assistantId || (anchor ? anchor.id : String(turn.userId || '')),
        text: turnCopyText(turn),
        turnIds: ids,
        turnLabel: `整轮（${ids.length}条）`,
      });
      return;
    }
    if ((item as { kind?: string }).kind === 'turn') {
      deps.menu.openMenu(e, {
        kind: 'assistant',
        id: String(item.group?.assistantId || item.group?.key || ''),
        text: String(item.group?.content || ''),
      });
      return;
    }
    deps.menu.openMenu(e, {
      kind: 'assistant',
      id: String(item.msg?.id || ''),
      text: String(item.msg?.content || ''),
    });
  }

  /** 乐观消息发送失败：整轮重试（新请求，不复用旧失败体）。 */
  function onRetryPending(): void {
    deps.retryTurn();
  }

  return { onToggle, onUserMenu, onAssistantMenu, onRetryPending };
}

export type TimelineRowActions = ReturnType<typeof useTimelineRowActions>;
