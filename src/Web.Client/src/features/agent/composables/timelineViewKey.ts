import { inject, provide } from 'vue';
import type { InjectionKey } from 'vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';

/** 流式段最小形态（模板只读这四个字段）。 */
export interface TimelineLiveSeg {
  kind: string;
  key: string;
  text: string;
  card: AgentToolCard | null;
}

/** 轮末遥测条（无活跃遥测时为 null，不渲染）。 */
export interface TimelineLiveTelemetry {
  protocol: string;
  ttft: number | null;
  input: number;
  cached: number;
  cachePercent: string;
  output: number;
}

/** 右键菜单浮层状态（行为走事件通道，状态只读下发）。 */
export interface TimelineMenuState {
  visible: boolean;
  x: number;
  y: number;
  busy: boolean;
  canCopy: boolean;
  canCopyTurn: boolean;
  copyLabel: string;
}

/**
 * 时间轴视图模型：装配层（MessageTimeline）一次性提供，
 * 行 / 轮 / 卡叶子按需取用，不再经多层 props 转手。
 */
export interface TimelineViewModel {
  summaryList: TurnSummary[] | null;
  pendingUser: import('@features/session/utils/pendingUser').PendingUser | null;
  hasActiveTurn: boolean;
  renderHtml: (text: string) => string;
  renderStream: (text: string) => string;
  anchorRef: (key: string) => (el: unknown) => void;
  liveSegsFor: (entry: TimelineEntry) => TimelineLiveSeg[];
  isGenerating: boolean;
  streamMdKillSwitch: boolean;
  readingWindow: boolean;
  liveCompleting: boolean;
  steadyReady: boolean;
  handoffAnim: boolean;
  singleProcess: boolean;
  noticeInTurn: boolean;
  turnNotice: string;
  lastError: string;
  showRetry: boolean;
  liveTelemetry: TimelineLiveTelemetry | null;
  menu: TimelineMenuState;
}

export const TimelineViewKey: InjectionKey<TimelineViewModel> = Symbol('timeline-view');

export function provideTimelineView(vm: TimelineViewModel): void {
  provide(TimelineViewKey, vm);
}

export function useTimelineView(): TimelineViewModel {
  const vm = inject(TimelineViewKey, null);
  if (!vm) throw new Error('[timeline] view model not provided');
  return vm;
}

/** 宽松读取：直挂单测无 provider 时回 null，调用方用本地缺省兜底。 */
export function useTimelineViewOptional(): TimelineViewModel | null {
  return inject(TimelineViewKey, null);
}

function identityText(t: string): string {
  return String(t || '');
}

/** 无 provider 时的纯展示兜底：只读渲染不断言、不抛错。 */
export function timelineViewFallback(): TimelineViewModel {
  return {
    summaryList: null,
    pendingUser: null,
    hasActiveTurn: false,
    renderHtml: identityText,
    renderStream: identityText,
    anchorRef: () => () => undefined,
    liveSegsFor: () => [],
    isGenerating: false,
    streamMdKillSwitch: false,
    readingWindow: false,
    liveCompleting: false,
    steadyReady: false,
    handoffAnim: true,
    singleProcess: true,
    noticeInTurn: false,
    turnNotice: '',
    lastError: '',
    showRetry: false,
    liveTelemetry: null,
    menu: { visible: false, x: 0, y: 0, busy: false, canCopy: false, canCopyTurn: false, copyLabel: '复制此消息' },
  };
}
