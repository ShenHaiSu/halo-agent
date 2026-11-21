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
  singleProcess: boolean;
}

/**
 * 装配视图模型：纯 V2 映射，只供摘要区 + Live 条 + 全局区。
 */
export function provideTimelineScene(t: SceneSource, opts: SceneOpts): TimelineViewModel {
  const viewModel = {
    get summaryList() {
      return opts.summaryList.value;
    },
    get pendingUser() {
      try {
        return t.turn.activeTurn?.pendingUser ?? null;
      } catch {
        return null;
      }
    },
    get hasActiveTurn() {
      return !!t.stream.hasActiveTurn;
    },
    renderHtml: opts.renderHtml,
    renderStream: opts.renderStream,
    get anchorRef() {
      return t.setAnchor;
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
    get steadyReady() {
      return !!t.steadyReady;
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
        canCopyTurn: t.menu.menuCanCopyTurn,
        copyLabel: t.menu.menuCopyLabel,
      };
    },
  } as TimelineViewModel;
  provideTimelineView(viewModel);
  return viewModel;
}
