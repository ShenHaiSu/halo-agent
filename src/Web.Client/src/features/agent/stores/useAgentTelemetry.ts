import { defineStore } from 'pinia';
import { computed } from 'vue';
import { haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import {
  activeSessionId,
  ensureSessionSync,
  ensureTurn,
  resolveTargetSession,
  touch,
  type AgentTelemetry,
  type TurnEventPayload,
} from './model';
import { agentRuntime } from './runtime';

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
    try {
      agentRuntime().applyUsagePatch(sid, evt.usage);
    } catch {
      /* 补丁永不阻断遥测 */
    }
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

  // 主链纯 V2：目录已切摘要源，旧投影不再计算（子会话走独立小链，不经此处）。

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
    bindTelemetry,
    recordPumpStats,
  };
});
