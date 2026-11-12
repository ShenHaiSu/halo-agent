import { defineStore } from 'pinia';
import { computed } from 'vue';
import { haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import { buildUserTurns, buildUserTurnsIncremental, createUserTurnsCache, type TimelineEntry } from '@shared/utils/turnGroup';
import { isP4SubOn } from '@features/need02/utils/need02Flags';
import {
  activeSessionId,
  ensureSessionSync,
  ensureTurn,
  persistedMessages,
  resolveTargetSession,
  touch,
  type AgentTelemetry,
  type TurnEventPayload,
} from './model';

let listenersBound = false;

function bindTelemetryListeners(): void {
  if (listenersBound) return;
  listenersBound = true;
  ensureSessionSync();
  haloEventBus.on<SessionStreamEventDto>('llm:stream-event', (evt) => {
    if (!evt || typeof evt.kind !== 'string') return;
    if (evt.kind !== 'UsageUpdated' || !evt.usage) return;
    const sid = resolveTargetSession(evt.sessionId);
    if (!sid) return;
    const t = ensureTurn(sid);
    t.telemetry.inputTokens = evt.usage.inputTokens || 0;
    t.telemetry.outputTokens = evt.usage.outputTokens || 0;
    t.telemetry.cachedTokens = evt.usage.cachedInputTokens || 0;
    t.telemetry.liveEst = 0;
    touch(t);
  });
  haloEventBus.on<TurnEventPayload>('agent:turn-event', (evt) => {
    if (!evt || typeof evt.kind !== 'string') return;
    if (evt.kind !== 'FirstToken') return;
    const sid = resolveTargetSession(evt.sessionId);
    if (!sid) return;
    const t = ensureTurn(sid);
    const p = evt.payload || {};
    t.telemetry.ttftMs = typeof p['ttftMs'] === 'number' ? (p['ttftMs'] as number) : 0;
    touch(t);
  });
}

export const useAgentTelemetry = defineStore('agent-telemetry', () => {
  bindTelemetryListeners();

  const activeTurnRef = computed(() => ensureTurn(activeSessionId.value));

  const telemetry = computed<AgentTelemetry>({
    get: () => activeTurnRef.value.telemetry,
    set: (v) => {
      activeTurnRef.value.telemetry = v;
    },
  });

  function persistedOf(): import('@shared/bridge/hostBridge').UnifiedChatMessage[] {
    return persistedMessages.value || [];
  }

  const userTurnsCache = createUserTurnsCache();
  const activeUserTurnEntries = computed<TimelineEntry[]>(() => {
    const msgs = persistedOf();
    let activeAssistantId: string | null = null;
    for (let i = msgs.length - 1; i >= 0; i--) {
      const m = msgs[i];
      if (String((m as unknown as { role?: unknown }).role || '').toLowerCase() === 'assistant') {
        activeAssistantId = String((m as unknown as { id?: unknown }).id || '');
        break;
      }
    }
    let isGenerating = false;
    let liveTotalMs: number | null = null;
    try {
      const t = ensureTurn(activeSessionId.value);
      isGenerating = t ? t.isGenerating === true : false;
      const ms = t ? t.telemetry.totalMs : null;
      liveTotalMs = typeof ms === 'number' ? ms : null;
    } catch {
      /* 读不到瞬态则按静态投影 */
    }
    const buildOpts = { activeAssistantId, isGenerating, liveTotalMs };
    try {
      if (isP4SubOn('proj')) return buildUserTurnsIncremental(msgs, buildOpts, userTurnsCache).entries as unknown as TimelineEntry[];
    } catch {
      /* 开关读取永不阻断投影 */
    }
    return buildUserTurns(msgs, buildOpts) as unknown as TimelineEntry[];
  });

  function bindTelemetry(): void {
    bindTelemetryListeners();
  }

  function recordPumpStats(stats: { polls: number; batches: number; gaps: number; resyncs: number }): void {
    try {
      const t = ensureTurn(activeSessionId.value);
      t.telemetry.pumpPolls = stats.polls;
      t.telemetry.pumpBatches = stats.batches;
      t.telemetry.pumpGaps = stats.gaps;
      t.telemetry.pumpResyncs = stats.resyncs;
      touch(t);
    } catch {
      /* stats must never break rendering */
    }
  }

  return {
    telemetry,
    activeUserTurnEntries,
    bindTelemetry,
    recordPumpStats,
  };
});
