import { computed } from 'vue';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAppearanceStore } from '@/stores/appearanceStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useAgentCards } from '@features/agent/stores/useAgentCards';
import { useAgentTelemetry } from '@features/agent/stores/useAgentTelemetry';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { isLiveOwner } from '@features/session/utils/turnRender';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import { need02Attr as buildAttr, useNeed02Flags } from '@features/need02/utils/need02Flags';
import { useTimelineMenu } from './useTimelineMenu';
import { useTimelineNav } from './useTimelineNav';
import { useTimelineExpansion } from './useTimelineExpansion';
import { useTimelineProjection } from './useTimelineProjection';
import type { DisplaySeg } from './useTimelineProjection';
import { useTimelineWindow } from './useTimelineWindow';
import { useTimelineCards } from './useTimelineCards';
import { useTimelineImages } from './useTimelineImages';
import { useTimelineBehavior } from './useTimelineBehavior';
export { provideTimelineView, useTimelineView, TimelineViewKey } from './timelineViewKey';
export type { TimelineViewModel } from './timelineViewKey';

/** 对外唯一扇出：装配投影/窗口/展开/卡片/图片 + 行为。 */
export function useTimelineViewModel() {
  const workspaceStore = useWorkspaceStore();
  const appearanceStore = useAppearanceStore();
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const cards = useAgentCards();
  const telemetry = useAgentTelemetry();
  const nav = useTimelineNav();
  const menu = useTimelineMenu();
  const need02Flags = useNeed02Flags();
  const { sessionMessages, budgetStatus, pendingOverlayMessages } = storeToRefs(workspaceStore);
  const chatWidthMode = computed(() => appearanceStore.chatWidthMode);
  const persistedMessages = computed<UnifiedChatMessage[]>(() => {
    try {
      const overlay = (pendingOverlayMessages as unknown as { value?: UnifiedChatMessage[] } | undefined)?.value;
      if (Array.isArray(overlay)) return overlay;
    } catch (e) {
      console.warn('[timeline] overlay read failed:', e);
    }
    return sessionMessages.value || [];
  });
  const lastPersistedAssistantId = computed<string>(() => {
    const msgs = persistedMessages.value;
    for (let i = msgs.length - 1; i >= 0; i--) {
      if (String(msgs[i].role || '').toLowerCase() === 'assistant') return String(msgs[i].id || '');
    }
    return '';
  });
  const budgetProtocol = computed<string>(() => String(budgetStatus.value?.protocol || telemetry.telemetry.protocol || 'chat'));
  const cardPart = useTimelineCards({ persistedMessages, need02Flags, budgetProtocol });
  const projection = useTimelineProjection({
    persistedMessages,
    lastPersistedAssistantId,
    need02Flags,
    segTextOf: cardPart.segTextOf,
    segCardOf: cardPart.segCardOf,
  });
  const expansion = useTimelineExpansion(projection.timelineItems);
  const images = useTimelineImages();
  const win = useTimelineWindow({ timelineItems: projection.timelineItems, historyEntries: projection.historyEntries, need02Flags, nav });
  const behavior = useTimelineBehavior({ win, projection, menu, nav });
  const need02Attr = computed<string>(() => buildAttr(need02Flags));
  const showLiveTelemetry = computed(() => {
    const t = telemetry.telemetry;
    return t.ttftMs !== null || t.totalMs !== null || t.inputTokens > 0 || t.outputTokens > 0;
  });
  const liveCachePercent = computed(() => {
    const t = telemetry.telemetry;
    return t.inputTokens <= 0 ? '-' : `${Math.round((t.cachedTokens / t.inputTokens) * 100)}%`;
  });
  return {
    chatWidthMode,
    persistedMessages,
    timelineItems: projection.timelineItems,
    historyEntries: projection.historyEntries,
    tailEntries: projection.tailEntries,
    visibleHistoryEntries: win.visibleHistoryEntries,
    hiddenAbove: win.hiddenAbove,
    resetMountWindow: win.resetMountWindow,
    ensureMountWindow: win.ensureMountWindow,
    growWindowTop: win.growWindowTop,
    handleTopReach: win.handleTopReach,
    anchorPrepend: win.anchorPrepend,
    bindScrollWindow: win.bindScrollWindow,
    unbindScrollWindow: win.unbindScrollWindow,
    onTranscriptScroll: win.onTranscriptScroll,
    pinnedRef: win.follow.pinnedRef,
    showJump: win.follow.showJump,
    releaseFollow: win.follow.releaseFollow,
    jumpToLatest: (cb?: () => void) => win.follow.jumpToLatest(() => win.pin.forceToBottom(), cb ?? win.clearReadingWindow),
    notifySend: () => {
      win.clearReadingWindow();
      if (win.paneVisible.value) win.follow.onSendRisingEdge(() => win.pin.forceToBottom());
    },
    paneVisible: win.paneVisible,
    setPaneVisible: win.setPaneVisible,
    readingWindow: win.readingWindow,
    setReadingWindow: win.setReadingWindow,
    clearReadingWindow: win.clearReadingWindow,
    liveInclusiveSegs: projection.liveInclusiveSegs,
    liveSegsFor: (entry: TimelineEntry, segs: DisplaySeg[]): DisplaySeg[] => {
      try {
        if (!isLiveOwner(entry, projection.timelineItems.value)) return [];
        return Array.isArray(segs) ? segs : [];
      } catch (e) {
        console.warn('[timeline] live owner check failed:', e);
        return [];
      }
    },
    transcriptRunning: win.transcriptRunning,
    isTurnExpanded: expansion.isTurnExpanded,
    toggleTurn: expansion.toggleTurn,
    smoothTailText: projection.smoothTailText,
    need02Flags,
    need02Attr,
    liveCompleting: computed(() => turn.activeTurn?.liveCompleting === true),
    handoffAnim: computed(() => need02Flags.p3),
    noticeInTurn: computed(() => need02Flags.p4),
    isRunExpanded: expansion.isRunExpanded,
    toggleRun: expansion.toggleRun,
    runByKey: expansion.runByKey,
    setAnchor: win.setAnchor,
    toolKeyOf: cardPart.toolKeyOf,
    toCard: cardPart.toCard,
    msgImages: images.msgImages,
    isPendingMessage: images.isPendingMessage,
    pendingStatusOf: images.pendingStatusOf,
    hasLostImage: images.hasLostImage,
    isImageLost: images.isImageLost,
    thumbOf: images.thumbOf,
    hasFailedTool: cardPart.hasFailedTool,
    failedToolCount: cardPart.failedToolCount,
    cachePercentOf: cardPart.cachePercentOf,
    hasTelemetryGroup: cardPart.hasTelemetryGroup,
    protocolOf: cardPart.protocolOf,
    cacheLineOf: cardPart.cacheLineOf,
    toolCount: cardPart.toolCount,
    curIteration: cardPart.curIteration,
    curIterToolCount: cardPart.curIterToolCount,
    showLiveTelemetry,
    liveCachePercent,
    onMarkdownLinkClick: behavior.onMarkdownLinkClick,
    onRetryTurn: behavior.onRetryTurn,
    onDismissError: behavior.onDismissError,
    attachContainer: behavior.attachContainer,
    detachContainer: behavior.detachContainer,
    menu,
    turn,
    stream,
    cards,
    telemetry,
    nav,
  };
}
export type TimelineViewModelState = ReturnType<typeof useTimelineViewModel>;
