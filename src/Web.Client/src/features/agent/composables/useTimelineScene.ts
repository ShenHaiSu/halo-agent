import type { ComputedRef, UnwrapNestedRefs } from 'vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { provideTimelineView } from './timelineViewKey';
import type { TimelineViewModel } from './timelineViewKey';
import type { TimelineViewModelState } from './useTimelineViewModel';

/** 装配源：聚合状态经响应式解包后的形态（与装配层本地读取一致）。 */
export type SceneSource = UnwrapNestedRefs<TimelineViewModelState>;

export interface SceneOpts {
  renderHtml: (text: string) => string;
  renderStream: (text: string) => string;
  summaryList: ComputedRef<TurnSummary[] | null>;
  hideLegacyHistory: ComputedRef<boolean>;
  singleProcess: boolean;
}

/**
 * 装配视图模型：把聚合状态映射为行/轮/卡叶子契约并逐层提供。
 * 纯映射层，不新增数据源；派生语义与旧模板逐一绑定完全一致。
 */
export function provideTimelineScene(t: SceneSource, opts: SceneOpts): TimelineViewModel {
  const viewModel = {
    get items() {
      return t.timelineItems;
    },
    get historyItems() {
      return t.visibleHistoryEntries;
    },
    get tailItems() {
      return t.tailEntries;
    },
    get summaryList() {
      return opts.summaryList.value;
    },
    get hideLegacyHistory() {
      return opts.hideLegacyHistory.value;
    },
    get hasActiveTurn() {
      return !!t.stream.hasActiveTurn;
    },
    renderHtml: opts.renderHtml,
    renderStream: opts.renderStream,
    get cardFor() {
      return t.toCard;
    },
    get toolKey() {
      return t.toolKeyOf;
    },
    get msgImages() {
      return t.msgImages;
    },
    get thumbOf() {
      return t.thumbOf;
    },
    get hasLost() {
      return t.hasLostImage;
    },
    get imgLost() {
      return t.isImageLost;
    },
    get isPending() {
      return t.isPendingMessage;
    },
    get pendingStatus() {
      return t.pendingStatusOf;
    },
    get isExpanded() {
      return t.isTurnExpanded;
    },
    get isRunExpanded() {
      return t.isRunExpanded;
    },
    get anchorRef() {
      return t.setAnchor;
    },
    get hasFailed() {
      return t.hasFailedTool;
    },
    get failedCount() {
      return t.failedToolCount;
    },
    get showTelemetry() {
      return t.hasTelemetryGroup;
    },
    get protocolOf() {
      return t.protocolOf;
    },
    get cacheLineOf() {
      return t.cacheLineOf;
    },
    liveSegsFor: (entry: Parameters<typeof t.liveSegsFor>[0]) => t.liveSegsFor(entry, t.liveInclusiveSegs),
    get isGenerating() {
      return t.transcriptRunning;
    },
    get streamMdKillSwitch() {
      return t.stream.streamMdKillSwitch;
    },
    get readingWindow() {
      return !!t.readingWindow;
    },
    get liveCompleting() {
      return !!t.liveCompleting;
    },
    get handoffAnim() {
      return !!t.handoffAnim;
    },
    get singleProcess() {
      return opts.singleProcess;
    },
    get noticeInTurn() {
      return !!t.noticeInTurn;
    },
    get turnNotice() {
      return t.turn.turnNotice;
    },
    get lastError() {
      return t.turn.lastError;
    },
    get showRetry() {
      return !!t.turn.activeTurn.lastErrorInfo?.retry;
    },
    get liveTelemetry() {
      if (!t.showLiveTelemetry) return null;
      const tele = t.telemetry.telemetry;
      return {
        protocol: tele.protocol === 'resp' ? 'RESP (/v1/responses)' : 'CHAT (/v1/chat/completions)',
        ttft: tele.ttftMs,
        input: tele.inputTokens,
        cached: tele.cachedTokens,
        cachePercent: t.liveCachePercent,
        output: tele.outputTokens,
      };
    },
    get menu() {
      return {
        visible: t.menu.menuState.visible,
        x: t.menu.menuState.x,
        y: t.menu.menuState.y,
        busy: t.menu.menuBusy,
        canCopy: t.menu.menuCanCopy,
      };
    },
  } as TimelineViewModel;
  provideTimelineView(viewModel);
  return viewModel;
}
