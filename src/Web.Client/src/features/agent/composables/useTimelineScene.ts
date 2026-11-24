import type { ComputedRef, UnwrapNestedRefs } from 'vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { provideTimelineView } from './timelineViewKey';
import type { TimelineLiveSeg, TimelineViewModel } from './timelineViewKey';
import type { TimelineViewModelState } from './useTimelineViewModel';
import { isHandoffEnabled, splitHandoffSegs } from '@features/agent/utils/handoffGrouping';

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
    get handoffActive() {
      // 交接窗 = 稳态已对账落屏 + Live 容器还在；热降级开关一票否决。
      return isHandoffEnabled() && !!t.liveCompleting && !!t.steadyReady;
    },
    get handoffFinalText() {
      // 正文全程用 Live 那一份；稳态盒首帧用同一文本作 markdown 种子，换手不闪 preview。
      const segs = (t.liveInclusiveSegs || []) as unknown as TimelineLiveSeg[];
      const { final } = splitHandoffSegs(segs);
      const body = final.map((s) => String(s.text || '')).join('');
      return body || String(t.stream.streamingText || '');
    },
    get handoffReduceHeight() {
      return isHandoffEnabled() && !!t.handoffAnim;
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
    get pendingReplay() {
      return t.turn.currentPendingReplay ?? null;
    },
    get menu() {
      const busy = t.menu.menuBusy;
      const reason = String(t.menu.menuState.target?.actionDisabledReason || '');
      const replayDisabled = busy || t.menu.menuState.target?.canReplay === false;
      const branchDisabled = busy || t.menu.menuState.target?.canBranch === false;
      return {
        visible: t.menu.menuState.visible,
        x: t.menu.menuState.x,
        y: t.menu.menuState.y,
        busy,
        canCopy: t.menu.menuCanCopy,
        canCopyTurn: t.menu.menuCanCopyTurn,
        copyLabel: t.menu.menuCopyLabel,
        replayDisabled,
        branchDisabled,
        replayTitle: reason || '截断该轮并重新生成',
        branchTitle: reason || '从这里创建分支会话',
      };
    },
  } as TimelineViewModel;
  provideTimelineView(viewModel);
  return viewModel;
}
