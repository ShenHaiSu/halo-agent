import {
  summaryAnchorKeys,
  summaryAssistantText,
  summaryCopyText,
  summaryHistoryAnchors,
  summaryUserText,
} from '@features/session/utils/summaryMenu';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { getHistoryCapabilitiesState } from '@shared/bridge/hostBridge';
import { trimBlankEdges } from '@shared/utils/trimBlank';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import type { TimelineMenu } from './useTimelineMenu';

interface RowActionDeps {
  menu: TimelineMenu;
  retryTurn: () => void;
  findSummary?: (turnId: string) => TurnSummary | null;
  getBlockMarkdown?: (blockId: string) => Promise<string>;
}

function historyAvailability(): { enabled: boolean; reason: string } {
  const state = getHistoryCapabilitiesState();
  if (!state.ready) return { enabled: false, reason: '历史能力尚未就绪，请重试' };
  if (!state.capabilities.historyAnchors) {
    return { enabled: false, reason: '当前后端版本不支持历史操作，请升级' };
  }
  return { enabled: true, reason: '' };
}

function availabilityReason(
  availability: { enabled: boolean; reason: string },
  summary: TurnSummary | null,
  anchorId: string,
  missingText: string
): { allowed: boolean; reason: string } {
  if (summary?.kind === 'compact') {
    return { allowed: false, reason: '压缩分隔行不支持重放或分支' };
  }
  if (!availability.enabled) return { allowed: false, reason: availability.reason };
  if (!anchorId) return { allowed: false, reason: missingText };
  return { allowed: true, reason: '' };
}

/** 摘要源行行为：复制仍用展示字段，破坏性操作只传真实消息 ID。 */
export function useTimelineRowActions(deps: RowActionDeps) {
  function onUserMenu(e: MouseEvent, item: TimelineEntry): void {
    const turnId = String(item.key || '').replace(/^live-/, '');
    const summary = deps.findSummary?.(turnId) ?? null;
    const availability = historyAvailability();
    if (summary) {
      const anchors = summaryHistoryAnchors(summary);
      const decision = availabilityReason(availability, summary, anchors.userId, '本轮没有可用的用户消息锚点');
      deps.menu.openTurnMenu(e, {
        kind: 'user',
        id: anchors.userId,
        text: summaryUserText(summary),
        turnText: summaryCopyText(summary, ''),
        copyLabel: '复制此提问',
        canReplay: decision.allowed,
        canBranch: decision.allowed,
        actionDisabledReason: decision.reason,
        draftText: summaryUserText(summary),
      });
      return;
    }
    const id = String(item.msg?.id || '');
    const text = trimBlankEdges(String(item.msg?.content || ''));
    deps.menu.openMenu(e, {
      kind: 'user',
      id,
      text,
      turnText: text,
      copyLabel: '复制此提问',
      canReplay: availability.enabled && !!id,
      canBranch: availability.enabled && !!id,
      actionDisabledReason: availability.enabled ? (id ? '' : '本轮没有可用的用户消息锚点') : availability.reason,
      draftText: text,
    });
  }

  async function onAssistantMenu(e: MouseEvent, item: TimelineEntry): Promise<void> {
    const turnId = String(item.key || '').replace(/^live-/, '');
    const summary = deps.findSummary?.(turnId) ?? null;
    const availability = historyAvailability();
    if (summary) {
      const anchors = summaryHistoryAnchors(summary);
      const decision = availabilityReason(availability, summary, anchors.assistantId, '本轮没有可重放的助手消息');
      const keys = summaryAnchorKeys(summary);
      let blocksMarkdown = '';
      try {
        if (keys.blockId && deps.getBlockMarkdown) blocksMarkdown = await deps.getBlockMarkdown(keys.blockId);
      } catch {
        /* 拉块失败降级用预览 */
      }
      const text = summaryAssistantText(summary, blocksMarkdown);
      const turnText = summaryCopyText(summary, blocksMarkdown);
      deps.menu.openTurnMenu(e, {
        kind: 'assistant',
        id: anchors.assistantId,
        text,
        turnText,
        copyLabel: '复制此回复',
        canReplay: decision.allowed,
        canBranch: decision.allowed,
        actionDisabledReason: decision.reason,
        draftText: '',
      });
      return;
    }
    const id = String(item.msg?.id || '');
    const text = trimBlankEdges(String(item.msg?.content || ''));
    deps.menu.openMenu(e, {
      kind: 'assistant',
      id,
      text,
      turnText: text,
      copyLabel: '复制此回复',
      canReplay: availability.enabled && !!id,
      canBranch: availability.enabled && !!id,
      actionDisabledReason: availability.enabled ? (id ? '' : '本轮没有可重放的助手消息') : availability.reason,
      draftText: '',
    });
  }

  function onRetryPending(): void {
    deps.retryTurn();
  }

  return { onUserMenu, onAssistantMenu, onRetryPending };
}

export type TimelineRowActions = ReturnType<typeof useTimelineRowActions>;
