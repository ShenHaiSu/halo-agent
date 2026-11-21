import { computed } from 'vue';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAppearanceStore } from '@/stores/appearanceStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useAgentTelemetry } from '@features/agent/stores/useAgentTelemetry';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import { need02Attr as buildAttr, useNeed02Flags } from '@features/need02/utils/need02Flags';
import { useTimelineMenu } from './useTimelineMenu';
import { useTimelineNav } from './useTimelineNav';
import { useTimelineWindow } from './useTimelineWindow';
import { useTimelineBehavior } from './useTimelineBehavior';
export { provideTimelineView, useTimelineView, TimelineViewKey } from './timelineViewKey';
export type { TimelineViewModel } from './timelineViewKey';

/** 对外唯一扇出：装配投影/窗口/展开/卡片/图片 + 行为。 */
export function useTimelineViewModel() {
  const workspaceStore = useWorkspaceStore();
  const appearanceStore = useAppearanceStore();
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const telemetry = useAgentTelemetry();
  const nav = useTimelineNav();
  const menu = useTimelineMenu();
  const need02Flags = useNeed02Flags();
  const { budgetStatus } = storeToRefs(workspaceStore);
  const chatWidthMode = computed(() => appearanceStore.chatWidthMode);
  void budgetStatus;
  // C4：投影已删除，窗口改用空数组过渡。
  const emptyItems = computed(() => [] as TimelineEntry[]);
  const win = useTimelineWindow({ timelineItems: emptyItems, historyEntries: emptyItems, need02Flags, nav });
  const behavior = useTimelineBehavior({ win, projection: null, menu, nav });
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
    liveInclusiveSegs: computed(() => {
      try {
        const segs = (turn.activeTurn?.segs || []) as Array<{ key?: unknown; kind?: unknown; text?: unknown; callId?: unknown }>;
        return segs.map((s, i) => ({
          kind: String((s as { kind?: unknown }).kind || ''),
          key: String((s as { key?: unknown }).key || `live-${i}`),
          text: String((s as { text?: unknown }).text || ''),
          card:
            String((s as { kind?: unknown }).kind || '') === 'tool'
              ? {
                  callId: String((s as { callId?: unknown }).callId || ''),
                  functionName: 'tool',
                  argsJson: '',
                  status: 'running' as const,
                  resultText: '',
                  exitCode: null,
                  durationMs: null,
                  iteration: 0,
                  receivedBytes: 0,
                  images: [],
                }
              : null,
        }));
      } catch {
        return [];
      }
    }),
    liveSegsFor: (
      entry: TimelineEntry,
      segs: { kind: string; key: string; text: string; card: unknown }[]
    ): { kind: string; key: string; text: string; card: unknown }[] => {
      try {
        // 直播挂载不需要轮容器：live 兜底条目直挂流段。
        if ((entry as TimelineEntry).kind === 'live') return Array.isArray(segs) ? segs : [];
        return [];
      } catch (e) {
        console.warn('[timeline] live owner check failed:', e);
        return [];
      }
    },
    transcriptRunning: win.transcriptRunning,
    need02Flags,
    need02Attr,
    liveCompleting: computed(() => turn.activeTurn?.liveCompleting === true),
    steadyReady: computed(() => turn.activeTurn?.steadyReady === true),
    handoffAnim: computed(() => need02Flags.p3),
    noticeInTurn: computed(() => need02Flags.p4),
    setAnchor: win.setAnchor,
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
    telemetry,
    nav,
  };
}
export type TimelineViewModelState = ReturnType<typeof useTimelineViewModel>;
