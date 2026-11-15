import { computed } from 'vue';
import type { ComputedRef, Ref } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { buildUserTurns, buildUserTurnsIncremental, createUserTurnsCache } from '@features/session/utils/userTurn';
import { isP4SubOn } from '@features/need02/utils/need02Flags';
import type { Need02Flags } from '@features/need02/utils/need02Flags';
import { syncTranscriptProjection } from '@features/session/utils/transcriptCache';
import { quotaLimit } from '@features/session/utils/timelineExpansion';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useSmoothText } from './useSmoothText';
import type { AgentToolCard } from '@features/agent/stores/model';

export interface DisplaySeg {
  kind: string;
  key: string;
  text: string;
  card: AgentToolCard | null;
}
const TAIL_SIZE = 8;
interface ProjectionDeps {
  persistedMessages: ComputedRef<UnifiedChatMessage[]> | Ref<UnifiedChatMessage[]>;
  lastPersistedAssistantId: ComputedRef<string> | Ref<string>;
  need02Flags: Need02Flags;
  segTextOf: (seg: { text?: unknown; key?: unknown; kind?: unknown; callId?: unknown } | null | undefined) => string;
  segCardOf: (seg: { text?: unknown; key?: unknown; kind?: unknown; callId?: unknown } | null | undefined) => AgentToolCard | null;
}
/** 投影唯一真相源：用户轮派生 + 历史/尾部分片 + 引用复用 + 内嵌流态。 */
export function useTimelineProjection(deps: ProjectionDeps) {
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const userTurnsCache = createUserTurnsCache();
  const timelineItems = computed<TimelineEntry[]>(() => {
    const msgs = deps.persistedMessages.value;
    let liveTotalMs: number | null = null;
    try {
      const ms = turn.activeTurn?.telemetry?.totalMs;
      liveTotalMs = typeof ms === 'number' ? ms : null;
    } catch (e) {
      console.warn('[timeline:projection] live total read failed:', e);
    }
    const buildOpts = {
      activeAssistantId: deps.lastPersistedAssistantId.value,
      isGenerating: turn.activeTurn?.isGenerating === true,
      liveTotalMs,
    };
    if (isP4SubOn('proj', null, deps.need02Flags))
      return buildUserTurnsIncremental(msgs, buildOpts, userTurnsCache).entries as unknown as TimelineEntry[];
    return buildUserTurns(msgs, buildOpts) as unknown as TimelineEntry[];
  });
  const entryRefs = new Map<string, TimelineEntry>();
  let projGeneration = 0;
  function reuseRef(e: TimelineEntry): TimelineEntry {
    const prev = entryRefs.get(e.key);
    if (!prev || prev.kind !== e.kind) {
      entryRefs.set(e.key, e);
      return e;
    }
    try {
      const prevMsgId = prev.msg ? String(prev.msg.id || '') : '';
      const curMsgId = e.msg ? String(e.msg.id || '') : '';
      const prevGroupKey = prev.group ? String(prev.group.key || prev.group.assistantId || '') : '';
      const curGroupKey = e.group ? String(e.group.key || e.group.assistantId || '') : '';
      if (prevMsgId === curMsgId && prevGroupKey === curGroupKey) {
        if (e.kind === 'user-turn') {
          const pt = (prev as unknown as { turn?: Record<string, unknown> }).turn;
          const ct = (e as unknown as { turn?: Record<string, unknown> }).turn;
          if (pt && ct) {
            const keys = [
              'userId',
              'stepCount',
              'toolCount',
              'failedCount',
              'contentChars',
              'reasoningChars',
              'isRunning',
              'phase',
              'endedAtUtc',
            ] as const;
            let same = true;
            for (const k of keys) {
              if (String(pt[k] ?? '') !== String(ct[k] ?? '')) {
                same = false;
                break;
              }
            }
            if (same) {
              const pSteps = Array.isArray((pt as { steps?: unknown[] }).steps) ? (pt as { steps: unknown[] }).steps.length : -1;
              const cSteps = Array.isArray((ct as { steps?: unknown[] }).steps) ? (ct as { steps: unknown[] }).steps.length : -2;
              if (pSteps === cSteps) return prev;
            }
          }
        } else return prev;
      }
    } catch (e) {
      console.warn('[timeline:projection] reuse compare failed:', e);
    }
    entryRefs.set(e.key, e);
    return e;
  }
  function pruneRefs(live: TimelineEntry[]): void {
    const limit = quotaLimit();
    if (entryRefs.size <= limit) return;
    const keep = new Set(live.map((e) => e.key));
    for (const k of entryRefs.keys()) if (!keep.has(k)) entryRefs.delete(k);
    if (entryRefs.size <= limit) return;
    for (const k of [...entryRefs.keys()]) {
      if (entryRefs.size <= limit) break;
      if (!keep.has(k)) entryRefs.delete(k);
    }
    while (entryRefs.size > limit) {
      const oldest = entryRefs.keys().next();
      if (oldest.done) break;
      entryRefs.delete(oldest.value);
    }
  }
  const historyEntries = computed<TimelineEntry[]>(() => {
    const all = timelineItems.value;
    const split = Math.max(0, all.length - TAIL_SIZE);
    const hist = all.slice(0, split).map(reuseRef);
    pruneRefs(all);
    try {
      syncTranscriptProjection(++projGeneration, hist, all.slice(split));
    } catch (e) {
      console.warn('[timeline:projection] cache sync failed:', e);
    }
    return hist;
  });
  const tailEntries = computed<TimelineEntry[]>(() => timelineItems.value.slice(Math.max(0, timelineItems.value.length - TAIL_SIZE)));
  const tailSegKey = computed<string>(() => {
    const segs = stream.visibleSegs;
    if (!segs.length) return '';
    return String(segs[segs.length - 1]?.key || '');
  });
  const tailTextSource = computed<string>(() => {
    const segs = stream.visibleSegs;
    if (!segs.length) return '';
    const tail = segs[segs.length - 1];
    return tail && tail.kind === 'text' ? String(tail.text || '') : '';
  });
  const smoothTailText = useSmoothText(tailTextSource, { active: computed(() => stream.isGenerating), key: tailSegKey });
  const liveInclusiveSegs = computed<DisplaySeg[]>(() => {
    let segs: Array<{ key?: unknown; kind?: unknown; text?: unknown }>;
    try {
      segs = (turn.activeTurn?.segs || []) as Array<{ key?: unknown; kind?: unknown; text?: unknown }>;
    } catch (e) {
      console.warn('[timeline:projection] live segs read failed:', e);
      return [];
    }
    if (!segs.length) return [];
    const lastIdx = segs.length - 1;
    return segs.map((s, i) => {
      const kind = String((s as { kind?: unknown }).kind || '');
      const key = String((s as { key?: unknown }).key || `live-${i}`);
      const isTailText = i === lastIdx && kind === 'text';
      return {
        kind,
        key,
        text: isTailText && tailSegKey.value === key ? smoothTailText.value : deps.segTextOf(s),
        card: kind === 'tool' ? deps.segCardOf(s) : null,
      };
    });
  });
  return { timelineItems, historyEntries, tailEntries, reuseRef, pruneRefs, tailSegKey, smoothTailText, liveInclusiveSegs };
}
export type TimelineProjection = ReturnType<typeof useTimelineProjection>;
export { TAIL_SIZE };
