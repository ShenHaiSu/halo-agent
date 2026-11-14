import type { InjectionKey } from 'vue';
import { inject, provide } from 'vue';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry } from '@features/session/utils/turnGroup';

export interface TimelineViewModel {
  items: TimelineEntry[];
  historyItems: TimelineEntry[];
  tailItems: TimelineEntry[];
  renderHtml: (t: string) => string;
  renderStream: (t: string) => string;
  cardFor: (m: UnifiedChatMessage, g: TimelineEntry['group'] & object) => unknown;
  toolKey: (m: UnifiedChatMessage) => string;
  isExpanded: (k: string) => boolean;
  isRunExpanded: (k: string) => boolean;
  anchorRef: (k: string) => (el: unknown) => void;
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
