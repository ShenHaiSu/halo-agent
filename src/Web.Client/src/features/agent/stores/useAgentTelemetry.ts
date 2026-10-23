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
  /**
   * need23-R7：去重集合 = **当前活动容器里真正可见的工具段**（seg.kind==='tool' && !seg.persisted）。
   * 修复前用的是整轮累计的 toolCards（只在 finishTurn 清空），导致已落盘的工具被永久拉黑：
   *   · 工具落盘 → 稳态折叠栏被剥空（"收起来"）
   *   · 下一轮新卡出现 → 又可见一下 → 落盘又消失（"又收起来"）
   * 现在：工具回包一落盘就脱离 hidden → 立即在稳态出现；活动容器同步让位，永不重复。
   */
  const hiddenToolCallIds = computed<Set<string>>(() => {
    const t = activeTurnRef.value;
    const out = new Set<string>();
    for (const s of t.segs || []) {
      if (s.kind !== 'tool' || s.persisted) continue;
      const cid = String(s.callId || '').trim();
      if (cid) out.add(cid);
    }
    return out;
  });
  const visibleTurnGroups = computed<TurnGroup[]>(() => {
    const hidden = hiddenToolCallIds.value;
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
