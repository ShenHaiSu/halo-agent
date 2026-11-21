import { summaryAnchorIds, summaryCopyText } from '@features/session/utils/summaryMenu';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { trimBlankEdges } from '@shared/utils/trimBlank';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import type { TimelineMenu } from './useTimelineMenu';

interface RowActionDeps {
  menu: TimelineMenu;
  retryTurn: () => void;
  findSummary?: (turnId: string) => TurnSummary | null;
  getBlockMarkdown?: (blockId: string) => Promise<string>;
}

/**
 * 摘要源行行为：复制/重放/分支/删除按轮锚点，不再经 V1 轮容器。
 */
export function useTimelineRowActions(deps: RowActionDeps) {
  /** 用户区右键：摘要用户全文直复制。 */
  function onUserMenu(e: MouseEvent, item: TimelineEntry): void {
    const turnId = String(item.key || '').replace(/^live-/, '');
    const summary = deps.findSummary?.(turnId) ?? null;
    if (summary) {
      const anchors = summaryAnchorIds(summary);
      const text = summaryCopyText(summary, '');
      deps.menu.openTurnMenu(e, { kind: 'user', id: anchors.userId, text, turnLabel: '整轮' });
      return;
    }
    deps.menu.openMenu(e, { kind: 'user', id: String(item.msg?.id || ''), text: trimBlankEdges(String(item.msg?.content || '')) });
  }

  /** 助手区右键：按块拉正文全文直复制。 */
  async function onAssistantMenu(e: MouseEvent, item: TimelineEntry): Promise<void> {
    const turnId = String(item.key || '').replace(/^live-/, '');
    const summary = deps.findSummary?.(turnId) ?? null;
    if (summary) {
      const anchors = summaryAnchorIds(summary);
      let blocksMarkdown = '';
      try {
        if (anchors.assistantId && deps.getBlockMarkdown) blocksMarkdown = await deps.getBlockMarkdown(anchors.assistantId);
      } catch {
        /* 拉块失败降级用预览 */
      }
      const text = summaryCopyText(summary, blocksMarkdown);
      deps.menu.openTurnMenu(e, { kind: 'assistant', id: anchors.assistantId, text, turnLabel: '整轮' });
      return;
    }
    deps.menu.openMenu(e, {
      kind: 'assistant',
      id: String(item.msg?.id || ''),
      text: trimBlankEdges(String(item.msg?.content || '')),
    });
  }

  /** 乐观消息发送失败：整轮重试（新请求，不复用旧失败体）。 */
  function onRetryPending(): void {
    deps.retryTurn();
  }

  return { onUserMenu, onAssistantMenu, onRetryPending };
}

export type TimelineRowActions = ReturnType<typeof useTimelineRowActions>;
