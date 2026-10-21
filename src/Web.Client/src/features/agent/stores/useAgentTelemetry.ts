/** need21-F03：遥测 store（最独立先拆；遥测/估算token/轮次归并投影）。
 * 跨 store 读：无（只读 model 单例 + 经旧转发同步的 persistedMessages 投影 + haloEventBus 事件）。
 */
import { defineStore } from 'pinia';
import { computed } from 'vue';
import { haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import { buildTurnGroups, buildTimelineEntries, type TurnGroup, type TimelineEntry } from '@shared/utils/turnGroup';
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
    t.telemetry.liveEst = 0; // need05-01：Usage 到账切真实，≈ 消失
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
  // setup 即绑定（旧 bind()/sendPrompt 内 ensureListeners 语义的上确界；幂等、无副作用放大）。
  bindTelemetryListeners();

  const activeTurnRef = computed(() => ensureTurn(activeSessionId.value));

  // ---- 兼容别名（模板/旧调用继续用，实际读写当前会话那桶） ----
  const telemetry = computed<AgentTelemetry>({
    get: () => activeTurnRef.value.telemetry,
    set: (v) => {
      activeTurnRef.value.telemetry = v;
    },
  });

  // ---- need22-P2 F1：单真相源归并（turns 瞬态 + persistedMessages 持久投影；Timeline 与目录共享） ----
  function persistedOf(): import('@shared/bridge/hostBridge').UnifiedChatMessage[] {
    return persistedMessages.value || [];
  }
  const activeTurnGroups = computed<TurnGroup[]>(() => buildTurnGroups(persistedOf()));
  const visibleTurnGroups = computed<TurnGroup[]>(() => {
    const hidden = new Set(activeTurnRef.value.toolCards.map((c) => c.callId).filter(Boolean));
    if (hidden.size === 0) return activeTurnGroups.value;
    const out: TurnGroup[] = [];
    for (const g of activeTurnGroups.value) {
      const kept = g.toolMessages.filter((m) => !hidden.has(String((m as unknown as { toolCallId?: string }).toolCallId || '')));
      if (!g.content && !g.reasoning && kept.length === 0) continue;
      out.push({ ...g, toolMessages: kept });
    }
    return out;
  });
  const activeTimelineEntries = computed<TimelineEntry[]>(() => buildTimelineEntries(persistedOf(), visibleTurnGroups.value));
  // 兼容别名：旧 hiddenToolCallIds 引用统一走 store 内归并
  const hiddenToolCallIds = computed<Set<string>>(() => new Set(activeTurnRef.value.toolCards.map((c) => c.callId).filter(Boolean)));

  function bindTelemetry(): void {
    bindTelemetryListeners();
  }

  return {
    telemetry,
    activeTurnGroups,
    visibleTurnGroups,
    activeTimelineEntries,
    hiddenToolCallIds,
    bindTelemetry,
  };
});
