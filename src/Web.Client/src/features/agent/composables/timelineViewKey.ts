import { inject, provide } from 'vue';
import type { InjectionKey } from 'vue';
import type { ChatImageRef, TurnSummary, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from '@features/session/utils/turnGroup';
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
}

/**
 * 时间轴视图模型：装配层（MessageTimeline）一次性提供，
 * 行 / 轮 / 卡叶子按需取用，不再经多层 props 转手。
 */
export interface TimelineViewModel {
  items: TimelineEntry[];
  historyItems: TimelineEntry[] | null;
  tailItems: TimelineEntry[] | null;
  summaryList: TurnSummary[] | null;
  hideLegacyHistory: boolean;
  hasActiveTurn: boolean;
  renderHtml: (text: string) => string;
  renderStream: (text: string) => string;
  cardFor: (m: UnifiedChatMessage, g: TurnGroup) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
  msgImages: (msg: UnifiedChatMessage | undefined) => ChatImageRef[];
  thumbOf: (img: ChatImageRef) => string;
  hasLost: (msg: UnifiedChatMessage | undefined) => boolean;
  imgLost: (img: ChatImageRef) => boolean;
  isPending: (msg: UnifiedChatMessage | undefined) => boolean;
  pendingStatus: (msg: UnifiedChatMessage | undefined) => string;
  isExpanded: (key: string) => boolean;
  isRunExpanded: (key: string) => boolean;
  anchorRef: (key: string) => (el: unknown) => void;
  hasFailed: (g: TurnGroup) => boolean;
  failedCount: (g: TurnGroup) => number;
  showTelemetry: (g: TurnGroup) => boolean;
  protocolOf: (g: TurnGroup) => string;
  cacheLineOf: (g: TurnGroup) => string;
  liveSegsFor: (entry: TimelineEntry) => TimelineLiveSeg[];
  isGenerating: boolean;
  streamMdKillSwitch: boolean;
  readingWindow: boolean;
  liveCompleting: boolean;
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

function emptyKey(m: UnifiedChatMessage): string {
  return String(m?.id || '');
}

/** 无 provider 时的纯展示兜底：只读渲染不断言、不抛错。 */
export function timelineViewFallback(): TimelineViewModel {
  return {
    items: [],
    historyItems: null,
    tailItems: null,
    summaryList: null,
    hideLegacyHistory: false,
    hasActiveTurn: false,
    renderHtml: identityText,
    renderStream: identityText,
    cardFor: (m: UnifiedChatMessage) => ({ callId: emptyKey(m), functionName: 'tool' }) as AgentToolCard,
    toolKey: emptyKey,
    msgImages: () => [],
    thumbOf: () => '',
    hasLost: () => false,
    imgLost: (img: ChatImageRef) => img?.lost === true,
    isPending: (msg) => String(msg?.id || '').startsWith('pending-'),
    pendingStatus: (msg) => (String(msg?.id || '').startsWith('pending-') ? 'sending' : ''),
    isExpanded: () => false,
    isRunExpanded: () => false,
    anchorRef: () => () => undefined,
    hasFailed: () => false,
    failedCount: () => 0,
    showTelemetry: () => false,
    protocolOf: () => '',
    cacheLineOf: () => '',
    liveSegsFor: () => [],
    isGenerating: false,
    streamMdKillSwitch: false,
    readingWindow: false,
    liveCompleting: false,
    handoffAnim: true,
    singleProcess: true,
    noticeInTurn: false,
    turnNotice: '',
    lastError: '',
    showRetry: false,
    liveTelemetry: null,
    menu: { visible: false, x: 0, y: 0, busy: false, canCopy: false },
  };
}
