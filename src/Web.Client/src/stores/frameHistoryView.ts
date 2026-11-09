import { computed } from 'vue';
import type { UnifiedChatMessage } from '@/shared/bridge/hostBridge';
import { buildTimelineEntries, buildTurnGroups, type TimelineEntry } from '@/features/session/utils/turnGroup';
import { countMissingImages, countToolCards } from './frameHistoryMap';
import { useFrameSessionStore } from './frameSessionStore';
import { HISTORY_LEGACY_BADGE, historyWriteRoute } from './historySourceGuard';
import { readOverride, resolveMode, type Surface, type SurfaceMode } from './surfaceRouter';

/**
 * Frame history view source. Every face now resolves through the single
 * surface table, which defaults to the frame chain; parking a face back on
 * the legacy chain is a one-row table flip. This seam only feeds the
 * frozen-history prop path.
 */

/** Pure history-face resolver with a test seam for the override list. */
export function resolveHistoryMode(override?: readonly Surface[]): SurfaceMode {
  try {
    return resolveMode('history', override);
  } catch {
    return 'legacy';
  }
}

/** Pure source pick: frame list only when the history face resolves to frame. */
export function selectHistoryMessages<T>(historyMode: SurfaceMode, legacyMessages: T[], frameMessages: T[]): T[] {
  return historyMode === 'frame' ? frameMessages : legacyMessages;
}

/**
 * Group mapped rows with the same pipeline the legacy chain uses, so tool
 * cards, reasoning and telemetry fold exactly like the baseline render.
 */
export function buildFrameEntries(messages: ReadonlyArray<UnifiedChatMessage>): TimelineEntry[] {
  const list = [...messages] as UnifiedChatMessage[];
  return buildTimelineEntries(list, buildTurnGroups(list));
}

export interface FrameParitySummary {
  rows: number;
  cards: number;
  missingImages: number;
  total: number;
}

/** Parity counters the view compares against the legacy baseline. */
export function summarizeFrameMessages(messages: ReadonlyArray<UnifiedChatMessage>, total: number): FrameParitySummary {
  return {
    rows: messages.length,
    cards: countToolCards(messages),
    missingImages: countMissingImages(messages),
    total,
  };
}

export function useFrameHistoryView(devOverride?: readonly Surface[]) {
  const frame = useFrameSessionStore();
  const mode = computed<SurfaceMode>(() => {
    if (devOverride !== undefined) return resolveHistoryMode(devOverride);
    return resolveHistoryMode(readOverride());
  });
  const active = computed(() => mode.value === 'frame');
  const messages = computed(() => (active.value ? frame.visibleHistory : []));
  const total = computed(() => (active.value ? frame.total : 0));
  const entries = computed<TimelineEntry[]>(() => (active.value ? buildFrameEntries(frame.visibleHistory) : []));
  const summary = computed(() => summarizeFrameMessages(active.value ? frame.visibleHistory : [], active.value ? frame.total : 0));
  const writeBadge = computed(() => {
    if (!active.value) return '';
    return historyWriteRoute() === 'legacy' ? HISTORY_LEGACY_BADGE : '';
  });
  return { mode, active, messages, total, entries, summary, writeBadge, frame };
}
