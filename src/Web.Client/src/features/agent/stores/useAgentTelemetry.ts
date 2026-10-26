/** need21-F03：遥测 store（最独立先拆；遥测/估算token/轮次归并投影）。
 * 跨 store 读：无（只读 model 单例 + 经旧转发同步的 persistedMessages 投影 + haloEventBus 事件）。
 */
import { defineStore } from 'pinia';
import { computed } from 'vue';
import { haloEventBus, type SessionStreamEventDto } from '@shared/bridge/hostBridge';
import { buildTurnGroupsIncremental, buildTimelineEntries, buildUserTurns, type TurnGroup, type TimelineEntry } from '@shared/utils/turnGroup';
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
  /**
   * M4-§3.2：把 buildTurnGroups 换成增量版本（`turnGroup.ts` 的 buildTurnGroupsIncremental）。
   *
   * 现状问题：`mergePersistedMessage` 每次替换 `sessionMessages` 数组引用，都会触发
   * activeTurnGroups → visibleTurnGroups → activeTimelineEntries 三级 computed 重算，
   * 而 `buildTurnGroups` 是**全量**重跑。多工具轮次（一轮 5 个工具）＝ 连续 5 次全量。
   *
   * 为什么把记忆体放在闭包里、而不是 `computed` 之外再开一个 ref + watchEffect：
   * 这里的记忆体是**纯 memo**，键就是输入数组的引用（`msgs === memoMsgs` 直接返回上次结果）。
   * 它不承载任何「依赖不变时也必须推进」的状态，因此即使 computed 的 getter 被跳过、
   * 直接返回缓存值，也不可能出现状态漂移（这正是 99 §14 警告的反模式，这里刻意规避）。
   * 回退条件由 buildTurnGroupsIncremental 自己保证：消息减少 / 末 assistant 落在旧前缀内
   * 且后缀非纯 tool 时，它内部整体回退到全量重建。
   */
  let memoMsgs: import('@shared/bridge/hostBridge').UnifiedChatMessage[] | null = null;
  let memoGroups: TurnGroup[] = [];
  const activeTurnGroups = computed<TurnGroup[]>(() => {
    const msgs = persistedOf();
    if (msgs === memoMsgs) return memoGroups;
    const prevCount = memoMsgs ? memoMsgs.length : 0;
    const r = buildTurnGroupsIncremental(memoGroups, prevCount, msgs);
    memoMsgs = msgs;
    memoGroups = r.groups;
    return memoGroups;
  });
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
  // need02-P2：用户轮次聚合投影（p2 on 时 useTimeline/Toc 消费此源；off 时回旧链）。
  // 运行态按“本轮含末条 live + 全局 generating”判定（03 §3.2，不再猜末 assistant，05 A3）。
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
    return buildUserTurns(msgs, { activeAssistantId, isGenerating, liveTotalMs }) as unknown as TimelineEntry[];
  });
  // 兼容别名：旧 hiddenToolCallIds 引用统一走 store 内归并

  function bindTelemetry(): void {
    bindTelemetryListeners();
  }

  return {
    telemetry,
    activeTurnGroups,
    visibleTurnGroups,
    activeTimelineEntries,
    activeUserTurnEntries,
    hiddenToolCallIds,
    bindTelemetry,
  };
});
