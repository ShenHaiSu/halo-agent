import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch, type ComponentPublicInstance } from 'vue';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAppearanceStore } from '@/stores/appearanceStore';
import { useUiStore } from '@/stores/uiStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useAgentCards } from '@features/agent/stores/useAgentCards';
import { useAgentTelemetry } from '@features/agent/stores/useAgentTelemetry';
import { getHostBridge, type ChatImageRef, type UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { imagesOf, orderedImagesOf, type TimelineEntry, type TurnGroup } from '@features/session/utils/turnGroup';
import { buildUserTurns } from '@features/session/utils/userTurn';
import { buildUserTurnsIncremental, createUserTurnsCache } from '@features/session/utils/userTurn';
import { isLiveOwner } from '@features/session/utils/turnRender';
import { isP4SubOn } from '@features/need02/utils/need02Flags';
import type { AgentToolCard } from '@features/agent/stores/model';
import { createBottomPin, PIN_TOLERANCE_PX, useFollowMode, useTimelineNav } from './useTimelineNav';
import { useSmoothText } from './useSmoothText';
import { syncTranscriptProjection } from '@features/session/utils/transcriptCache';
import { INITIAL_MOUNT, STEP, WINDOW_MIN, hiddenAboveByWeight, isNearTop } from '@features/session/utils/transcriptWindow';
import { useTimelineMenu } from './useTimelineMenu';
import { need02Attr as buildNeed02Attr, useNeed02Flags } from '@features/need02/utils/need02Flags';
import { pendingPreviewOf } from '@features/session/utils/pendingUser';
import { quotaLimit } from '@features/session/utils/timelineExpansion';
export interface DisplaySeg {
  kind: string;
  key: string;
  text: string;
  card: AgentToolCard | null;
}
export function useTimeline() {
  const workspaceStore = useWorkspaceStore();
  const appearanceStore = useAppearanceStore();
  const uiStore = useUiStore();
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const cards = useAgentCards();
  const telemetry = useAgentTelemetry();
  const nav = useTimelineNav();
  const menu = useTimelineMenu();
  const { sessionMessages, budgetStatus, pendingOverlayMessages } = storeToRefs(workspaceStore);
  const chatWidthMode = computed(() => appearanceStore.chatWidthMode);
  const scrollEl = ref<HTMLElement | null>(null);
  // bigChange P2-3: pinned/showJump dual mode; hidden pane never follows/pages/samples.
  // M2-2：`follow` 必须声明在 `pin` 之前——`createBottomPin` 的 isFollowing 回调要用它（否则 TDZ）。
  const follow = useFollowMode();
  const pin = createBottomPin(() => scrollEl.value, {
    isLocked: () => performance.now() < uiStore.jumpLockUntil,
    release: () => uiStore.releaseJumpLock(),
    isFollowing: () => follow.pinnedRef.value === true,
  });
  /** need02-P1：persistedMessages 改读 overlay（含 pending）；p1 off 时 overlay 恒等于 sessionMessages，零行为变更。 */
  const persistedMessages = computed<UnifiedChatMessage[]>(() => {
    try {
      const overlay = (pendingOverlayMessages as unknown as { value?: UnifiedChatMessage[] } | undefined)?.value;
      if (Array.isArray(overlay)) return overlay;
    } catch {
      /* fallthrough */
    }
    return sessionMessages.value || [];
  });
  const expandedTurns = reactive<Record<string, boolean>>({});
  const isTurnExpanded = (key: string): boolean => expandedTurns[key] === true;
  const toggleTurn = (key: string): void => {
    expandedTurns[key] = !isTurnExpanded(key);
  };
  // 用户轮次投影是唯一时间轴数据源。
  const need02Flags = useNeed02Flags();
  const need02Attr = computed<string>(() => buildNeed02Attr(need02Flags));
  // 末条已落盘 assistant，供运行态判定与交接锚点使用。
  const lastPersistedAssistantId = computed<string>(() => {
    const msgs = persistedMessages.value;
    for (let i = msgs.length - 1; i >= 0; i--) {
      const m = msgs[i] as UnifiedChatMessage;
      if (String(m.role || '').toLowerCase() === 'assistant') return String(m.id || '');
    }
    return '';
  });
  // 用户轮次投影（overlay 为唯一真相源；增量与全量由 p4-proj 子开关选择）。
  const userTurnsCache = createUserTurnsCache();
  const timelineItems = computed<TimelineEntry[]>(() => {
    const msgs = persistedMessages.value;
    let liveTotalMs: number | null = null;
    try {
      const ms = turn.activeTurn?.telemetry?.totalMs;
      liveTotalMs = typeof ms === 'number' ? ms : null;
    } catch {
      /* ignore */
    }
    const buildOpts = {
      activeAssistantId: lastPersistedAssistantId.value,
      isGenerating: turn.activeTurn?.isGenerating === true,
      liveTotalMs,
    };
    if (isP4SubOn('proj', null, need02Flags)) {
      return buildUserTurnsIncremental(msgs, buildOpts, userTurnsCache).entries as unknown as TimelineEntry[];
    }
    return buildUserTurns(msgs, buildOpts) as unknown as TimelineEntry[];
  });
  // 用户轮次展开索引（运行中默认展开、终态默认收起）。
  const runByKey = computed<Map<string, { key: string; isRunning: boolean }>>(() => {
    const m = new Map<string, { key: string; isRunning: boolean }>();
    for (const e of timelineItems.value) {
      if (e.kind === 'user-turn' && (e as unknown as { turn?: { key?: unknown; isRunning?: unknown } }).turn) {
        const t = (e as unknown as { turn: { key: string; isRunning: boolean } }).turn;
        m.set(e.key, { key: String(t.key || e.key), isRunning: t.isRunning === true });
      }
    }
    return m;
  });
  /** 用户显式选择过的合并舱展开态（未命中时按「运行中展开、终态收起」——决策 D2 = A）。 */
  const runExplicit = reactive<Record<string, boolean>>({});
  const isRunExpanded = (key: string): boolean => {
    if (key in runExplicit) return runExplicit[key] === true;
    const hit = runByKey.value.get(key);
    return !!hit && hit.isRunning;
  };
  const toggleRun = (key: string): void => {
    const next = !isRunExpanded(key);
    runExplicit[key] = next;
    expandedTurns[key] = next;
  };
  // bigChange P2-1/P2-3: history memo frozen + tail live. key = message/turn id; reuseRef keeps refs stable.
  // S3 G-2: weight converged per box (1+steps); count-based chain kept for v1 compat only.
  const TAIL_SIZE = 8;
  const entryRefs = new Map<string, TimelineEntry>();
  let projGeneration = 0;
  function reuseRef(e: TimelineEntry): TimelineEntry {
    const prev = entryRefs.get(e.key);
    if (!prev) {
      entryRefs.set(e.key, e);
      return e;
    }
    if (prev.kind !== e.kind) {
      entryRefs.set(e.key, e);
      return e;
    }
    // need02-P1（05 R2–R3）：pending 转正引用会变但 key 不变，必须按 key+id 复用。
    // 旧逻辑只认 `msg/group` 引用，pending 期每次 toPendingMessage 新建对象会导致 DOM 重建（spinner 闪）。
    // 这里改按 id+内容键复用：id 相同且 group 键相同即复用旧引用，保证 pending 期零重建。
    try {
      const prevMsgId = prev.msg ? String(prev.msg.id || '') : '';
      const curMsgId = e.msg ? String(e.msg.id || '') : '';
      const prevGroupKey = prev.group ? String(prev.group.key || prev.group.assistantId || '') : '';
      const curGroupKey = e.group ? String(e.group.key || e.group.assistantId || '') : '';
      if (prevMsgId === curMsgId && prevGroupKey === curGroupKey) {
        // need02-P2：user-turn 条目无 msg/group，改按 turn 指纹复用（引用新建但语义不变时复用旧引用，DOM 不重建）。
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
        } else {
          return prev;
        }
      }
    } catch {
      /* fallthrough 重建 */
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
    const keys = [...entryRefs.keys()];
    for (const k of keys) {
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
    } catch {
      /* cache sync never blocks render */
    }
    return hist;
  });
  const tailEntries = computed<TimelineEntry[]>(() => {
    const all = timelineItems.value;
    return all.slice(Math.max(0, all.length - TAIL_SIZE));
  });
  // bigChange P2-3: pinned/showJump dual mode（`follow` 已在 pin 之前声明，见上）
  const paneVisible = ref(true);
  function setPaneVisible(v: boolean): void {
    paneVisible.value = v;
  }
  // readingWindow: historical focus degrades running lights + hides action cards.
  const readingWindow = ref(false);
  function setReadingWindow(v: boolean): void {
    readingWindow.value = v;
  }
  function clearReadingWindow(): void {
    readingWindow.value = false;
  }
  const transcriptRunning = computed(() => stream.isGenerating && !readingWindow.value);
  // Windowed top expansion: near-top first grows +STEP, window-covered only then loadOlder; prepend anchors via scrollTop += delta.
  const hiddenAbove = ref(0);
  const visibleHistoryEntries = computed<TimelineEntry[]>(() => historyEntries.value.slice(hiddenAbove.value));
  /** need02-P4（p4-window）：条目权重（user-turn 按 1+steps，余按 1；见 transcriptWindow.turnWeightOf）。 */
  function weightOfItem(e: TimelineEntry): number {
    try {
      const u = e as unknown as { kind?: string; turn?: { steps?: unknown[] } };
      if (u && u.kind === 'user-turn') return 1 + (Array.isArray(u.turn?.steps) ? (u.turn as { steps: unknown[] }).steps.length : 0);
    } catch {
      /* fallthrough */
    }
    return 1;
  }
  function resetMountWindow(): void {
    // need02-P4（p4-window）：首屏按权重预算挂载（TURN_WINDOW_WEIGHT≈15 平均轮），超高轮仍整轮挂载；
    // off 时回条数旧链（INITIAL_MOUNT/WINDOW_MIN）。
    // 切会话只调此处：只算 hiddenAbove，不碰 scrollTop（停顶部由调用方显式 scrollTop = 0）。
    if (isP4SubOn('window', null, need02Flags)) {
      hiddenAbove.value = hiddenAboveByWeight(
        timelineItems.value.map((e) => ({
          key: e.key,
          kind: String((e as unknown as { kind?: string }).kind || ''),
          steps: weightOfItem(e) - 1,
        }))
      );
    } else {
      hiddenAbove.value = Math.max(0, timelineItems.value.length - INITIAL_MOUNT);
    }
  }
  function ensureMountWindow(): void {
    resetMountWindow();
    requestAnimationFrame(() => {
      if (!follow.pinnedRef.value || !paneVisible.value) return;
      hiddenAbove.value = Math.max(0, timelineItems.value.length - WINDOW_MIN);
      pin.forceToBottom();
    });
  }
  /**
   * 向上扩窗。返回 true 表示可见窗口真的变大了（调用方需据此做顶部锚定补偿）。
   *
   * 注意：`hiddenAbove` 一旦为 0，`visibleHistoryEntries` 已覆盖全部已加载条目，
   * 此时再往上滚就是真的「到顶」——放手让 handleTopReach 走 loadOlder 分支。
   */
  function growWindowTop(step = STEP): boolean {
    if (hiddenAbove.value <= 0) return false;
    // need02-P4（p4-window）：按权重扩窗（向前累计权重≥step 的条数，至少 1 条）；off 时回条数旧链。
    if (isP4SubOn('window', null, need02Flags)) {
      const items = timelineItems.value;
      let acc = 0;
      let n = 0;
      for (let i = hiddenAbove.value - 1; i >= 0 && acc < step; i--) {
        acc += weightOfItem(items[i] as TimelineEntry);
        n++;
      }
      hiddenAbove.value = Math.max(0, hiddenAbove.value - Math.max(1, n));
      return true;
    }
    hiddenAbove.value = Math.max(0, hiddenAbove.value - step);
    return true;
  }
  function anchorPrepend(el: HTMLElement | null, beforeHeight: number): void {
    if (!el) return;
    try {
      const delta = el.scrollHeight - beforeHeight;
      if (delta > 0) el.scrollTop += delta;
    } catch (e) {
      console.warn('[timeline] anchor prepend failed:', e);
    }
  }
  async function handleTopReach(loadOlder: () => void | Promise<void>): Promise<void> {
    if (!paneVisible.value) return;
    if (growWindowTop()) return;
    const el = scrollEl.value;
    const before = el ? el.scrollHeight : 0;
    await loadOlder();
    await nextTick();
    anchorPrepend(el, before);
  }
  /** 顶部扩窗 + 锚定。同步路径（无需 loadOlder 的场景）用，避免 async 竞态。 */
  function expandTopWithAnchor(step = STEP): boolean {
    const el = scrollEl.value;
    const before = el ? el.scrollHeight : 0;
    if (!growWindowTop(step)) return false;
    // 新增条目渲染在顶部 → 内容高度增加，必须等 DOM 落地再补 scrollTop，否则视口被「顶」上去。
    //
    // 注意：这里**绝不**调 pin.scheduleToBottom()。用户会滚到近顶说明他在往上读历史，
    // 此时把他拽回底部既违背意图、又会让扩窗白做（刚加载的更早内容立刻滚出视口）。
    // 「仍贴底时扩窗后回到底部」是 ensureMountWindow 的职责，与顶部扩窗互斥。
    // 切会话路径只调 resetMountWindow（纯算窗口、不拽底），与此处互斥关系不变。
    void nextTick(() => {
      anchorPrepend(scrollEl.value, before);
    });
    return true;
  }
  /**
   * 滚动事件入口（由 bindScrollWindow 注册）。
   *
   * 修复点：此前 growWindowTop/handleTopReach 只有定义、没有任何事件源，
   * 导致 hiddenAbove 打开会话后就永久固定，用户滚到顶也无法看到更早的历史。
   *
   * 这里同时补上 pinned 的几何判定（此前只有 wheel/touchstart/keydown 的意图判定，
   * 键盘 Home/End、滚动条拖拽、程序化滚动都感知不到 → R4「上翻被拽回底部」隐患）。
   */
  const SCROLL_IDLE_MS = 120;
  /**
   * 单次「触顶会话」内最多连续扩窗轮数。
   *
   * 为什么必须有这个兜底：限流原本只靠「锚定补偿把 scrollTop 抬出近顶区」，
   * 但那依赖「新增条目撑高 > NEAR_TOP_PX」——消息很短时（例如多条一行 system 提示）
   * 一轮只撑高几十 px，仍留在近顶区，于是每 120ms 又扩一轮，一瞬间就扩到全量。
   * 用户离开近顶区（scrollTop > 阈值）时计数清零，下次触顶重新计。
   */
  const MAX_GROW_PER_REACH = 3;
  let scrollRaf = 0;
  let topGrowAt = 0;
  let growThisReach = 0;
  function onTranscriptScroll(): void {
    if (scrollRaf) return;
    scrollRaf = requestAnimationFrame(() => {
      scrollRaf = 0;
      const el = scrollEl.value;
      if (!el || !paneVisible.value) return;
      const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
      // 几何跟随判定：只在状态与几何不一致时翻转，避免流式期间抖动。
      const pinnedNow = distanceFromBottom <= PIN_TOLERANCE_PX;
      const wasPinned = follow.pinnedRef.value;
      if (pinnedNow !== wasPinned) {
        // 用户主动往回贴底 → 重新进入跟随（回到底部按钮消失）；反之解除跟随并亮出按钮。
        // 贴底分支用 ingest 的**状态部分**：几何已在位，不再调 pin.forceToBottom() 写
        // scrollTop —— 那会二次触发本回调，形成隐式耦合。
        // 同时清 readingWindow：回到底部即「离开历史阅读模式」，与 jumpToLatest 语义对齐。
        if (pinnedNow) {
          follow.ingest(distanceFromBottom, 'unknown');
          clearReadingWindow();
        } else {
          follow.releaseFollow();
        }
      }
      if (!isNearTop(el.scrollTop)) {
        growThisReach = 0; // 已离开近顶区 → 本轮触顶结束，下次重新计数
        return;
      }
      if (growThisReach >= MAX_GROW_PER_REACH) return;
      // 向上扩窗天然限流：扩窗后 scrollTop 被锚定补偿抬回近顶阈值之上，
      // 滚动事件不会连续刷屏；这里的 120ms 只是防「滚轮惯性」在同一帧内连发。
      const now = performance.now();
      if (now - topGrowAt < SCROLL_IDLE_MS) return;
      topGrowAt = now;
      if (expandTopWithAnchor()) growThisReach++;
    });
  }
  function bindScrollWindow(): void {
    const el = scrollEl.value;
    if (!el) return;
    el.addEventListener('scroll', onTranscriptScroll, { passive: true });
  }
  function unbindScrollWindow(): void {
    const el = scrollEl.value;
    if (!el) return;
    try {
      el.removeEventListener('scroll', onTranscriptScroll);
    } catch (e) {
      console.warn('[timeline] unbind scroll failed:', e);
    }
    if (scrollRaf) cancelAnimationFrame(scrollRaf);
    scrollRaf = 0;
  }
  function setAnchor(key: string) {
    return (el: Element | ComponentPublicInstance | null) => {
      try {
        if (el instanceof HTMLElement) nav.register(key, el);
        else nav.unregister(key);
      } catch (e) {
        console.warn('[timeline] anchor failed:', e);
      }
    };
  }
  const toolKeyOf = (m: UnifiedChatMessage): string => String((m as unknown as { toolCallId?: string }).toolCallId || m.id);
  // need02-P4（p4-proj）：toCard 派生一次算好、模板零 find/reduce。
  // 原实现每次 computed 重跑都要 find + 切片（长轮次打字机卡顿，01 A5）；此处按 callId+内容指纹记忆化，
  // 有界 300（LRU 淘汰最老）。p4-proj=off 时回直算旧链。注意：只缓存纯派生，不改传入对象（05 R1）。
  const cardCache = new Map<string, AgentToolCard>();
  function cardCacheKey(callId: string, content: string, fnName: string): string {
    let h = 0x811c9dc5;
    const s = String(content || '');
    const head = s.length > 512 ? s.slice(0, 512) : s;
    for (let i = 0; i < head.length; i++) {
      h ^= head.charCodeAt(i);
      h = Math.imul(h, 0x01000193);
    }
    return `${callId}|${s.length}|${(h >>> 0).toString(16)}|${fnName}`;
  }
  function toCard(m: UnifiedChatMessage, group: TurnGroup): AgentToolCard {
    const callId = String((m as unknown as { toolCallId?: string }).toolCallId || m.id);
    const content = String(m.content || '');
    const useCache = isP4SubOn('proj', null, need02Flags);
    const fnName = (() => {
      const ref = group.toolCalls.find((t) => t.id === callId);
      return ref?.name || (m.name as string) || 'tool';
    })();
    if (useCache) {
      const key = cardCacheKey(callId, content, fnName);
      const hit = cardCache.get(key);
      if (hit) {
        cardCache.delete(key);
        cardCache.set(key, hit);
        return hit;
      }
    }
    const card = toCardUncached(m, group, callId, content, fnName);
    if (useCache) {
      cardCache.set(cardCacheKey(callId, content, fnName), card);
      if (cardCache.size > 300) {
        const oldest = cardCache.keys().next();
        if (!oldest.done) cardCache.delete(oldest.value);
      }
    }
    return card;
  }
  function toCardUncached(m: UnifiedChatMessage, group: TurnGroup, callId: string, content: string, fnName: string): AgentToolCard {
    let argsJson = '';
    const rawAssistant = persistedMessages.value.find((x) => String(x.id) === group.assistantId);
    const rawCalls = (
      rawAssistant as unknown as
        | {
            toolCalls?: Array<{
              id?: string;
              callId?: string;
              argumentsJson?: string;
              function?: { arguments?: string; name?: string };
              functionName?: string;
            }>;
          }
        | null
        | undefined
    )?.toolCalls;
    if (Array.isArray(rawCalls)) {
      const hit = rawCalls.find((c) => String(c.id || c.callId) === callId);
      if (hit) argsJson = String(hit.argumentsJson || hit.function?.arguments || '');
    }
    return {
      callId,
      functionName: fnName || (m.name as string) || 'tool',
      argsJson,
      status: content.includes('[Tool Error') ? 'error' : 'done',
      resultText: content.length > 6000 ? content.slice(0, 6000) + '\n...（输出过长已截断）' : content,
      exitCode: null,
      durationMs: null,
      iteration: 0,
      receivedBytes: 0,
      images: imagesOf(m),
    };
  }
  const thumbnailCache = ref<Record<string, string>>({});
  const lostImageIds = ref<Record<string, boolean>>({});
  const thumbPending = new Set<string>();
  /** S6 §4.3：转录图片行顺序与发包一致（按占位符顺序；无占位符退化 images 数组序）。 */
  const msgImages = (msg: UnifiedChatMessage | undefined): ChatImageRef[] => orderedImagesOf(msg);
  /** need02-P1：是否 pending 消息（id 前缀 `pending-`，仅视图层合成，不写 jsonl）。 */
  const isPendingMessage = (msg: UnifiedChatMessage | undefined): boolean => {
    const id = String(msg?.id || '');
    return id.startsWith('pending-');
  };
  /** need02-P1：pending 状态（sending/failed），供 TimelineRow 右下小 spinner/重发用；非 pending 返回 ''。 */
  const pendingStatusOf = (msg: UnifiedChatMessage | undefined): string => {
    try {
      if (!isPendingMessage(msg)) return '';
      const p = turn.activeTurn?.pendingUser;
      if (p && String(p.key || '') === String(msg?.id || '')) return String(p.status || 'sending');
      // 切会话/重载后 turn 桶可能已换，但 overlay 仍带 pending 消息（极短窗口）：按 sending 展示。
      return 'sending';
    } catch {
      return '';
    }
  };
  const hasLostImage = (msg: UnifiedChatMessage | undefined): boolean => {
    // need02-P1：pending 期恒 false（本地预览必在，不判丢失）。
    if (isPendingMessage(msg)) return false;
    return msgImages(msg).some((i) => lostImageIds.value[i.imageId] || i.lost === true);
  };
  /** M1-4：单图丢失判定（供 TimelineRow 决定渲染标签槽位还是加载骨架）。 */
  const isImageLost = (img: ChatImageRef): boolean => {
    // need02-P1：本地预览存在时永不判丢失。
    try {
      const p = turn.activeTurn?.pendingUser;
      if (p && p.previews && String(p.previews[String(img?.imageId || '')] || '')) return false;
    } catch {
      /* fallthrough */
    }
    return img.lost === true || lostImageIds.value[img.imageId] === true;
  };
  function thumbOf(img: ChatImageRef): string {
    // need02-P1：pending 期直出本地 dataURL，不走缩略图懒取（复用 96px 槽，零高度跳变）。
    try {
      const p = turn.activeTurn?.pendingUser;
      if (p) {
        const preview = pendingPreviewOf(p, String(img?.imageId || ''));
        if (preview) return preview;
      }
    } catch {
      /* fallthrough 正常懒取 */
    }
    if (thumbnailCache.value[img.imageId] || img.lost === true || lostImageIds.value[img.imageId])
      return thumbnailCache.value[img.imageId] || '';
    if (thumbPending.has(img.imageId)) return '';
    thumbPending.add(img.imageId);
    // need02-P4：缩略图合批（requestIdleCallback 闲时每批≤6，长会话图片墙不再 N 路并发打满 bridge）。
    // p4=off 时回立即单取旧链（本项无子开关，跟 master，见 04-P4#6）。失败 lost 缓存（已有语义保持）。
    if (need02Flags.p4) enqueueThumb(img.imageId);
    else void fetchThumb(img.imageId);
    return '';
  }
  const thumbQueue: string[] = [];
  let thumbIdleScheduled = false;
  const THUMB_BATCH = 6;
  function scheduleThumbIdle(): void {
    if (thumbIdleScheduled) return;
    thumbIdleScheduled = true;
    const run = (): void => {
      thumbIdleScheduled = false;
      const batch = thumbQueue.splice(0, THUMB_BATCH);
      for (const id of batch) void fetchThumb(id);
      if (thumbQueue.length > 0) scheduleThumbIdle();
    };
    try {
      const ric = (window as unknown as { requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number })
        .requestIdleCallback;
      if (typeof ric === 'function') ric.call(window, run, { timeout: 300 });
      else window.setTimeout(run, 0);
    } catch {
      window.setTimeout(run, 0);
    }
  }
  function enqueueThumb(imageId: string): void {
    if (!thumbQueue.includes(imageId)) thumbQueue.push(imageId);
    scheduleThumbIdle();
  }
  async function fetchThumb(imageId: string): Promise<void> {
    try {
      const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
      const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
      const res = await getHostBridge().getImageThumbnail(ws, sid, imageId);
      if (res.ok && typeof res.data === 'string' && res.data) thumbnailCache.value = { ...thumbnailCache.value, [imageId]: res.data };
      else lostImageIds.value = { ...lostImageIds.value, [imageId]: true };
    } catch {
      lostImageIds.value = { ...lostImageIds.value, [imageId]: true };
    } finally {
      thumbPending.delete(imageId);
    }
  }
  /** 该轮内失败的 tool 条数（单一数据源：判定口径与 hasFailedTool 完全同源）。 */
  const failedToolCount = (g: TurnGroup): number =>
    (g.toolMessages || []).reduce((n, m) => n + (String(m.content || '').includes('[Tool Error') ? 1 : 0), 0);
  const hasFailedTool = (g: TurnGroup): boolean => failedToolCount(g) > 0;
  const cachePercentOf = (cached: number, input: number): string => (!input || input <= 0 ? '-' : `${Math.round((cached / input) * 100)}%`);
  const hasTelemetryGroup = (g: TurnGroup): boolean =>
    !!g.telemetry && (g.telemetry.inputTokens != null || g.telemetry.outputTokens != null);
  function protocolOf(g: TurnGroup): string {
    void g;
    return ((budgetStatus.value?.protocol || telemetry.telemetry.protocol || 'chat').toLowerCase() === 'resp' ? 'RESP' : 'CHAT') + '';
  }
  function cacheLineOf(g: TurnGroup): string {
    return `IN ${g.telemetry?.inputTokens ?? '-'} · Cache ${g.telemetry?.cachedTokens ?? 0} · ${cachePercentOf(g.telemetry?.cachedTokens ?? 0, g.telemetry?.inputTokens ?? 0)}`;
  }
  function segTextOf(seg: { text?: string } | null | undefined): string {
    try {
      return stream.segDisplayText(seg as unknown as Parameters<typeof stream.segDisplayText>[0]);
    } catch (e) {
      console.warn('[timeline] seg text failed:', e);
      return String(seg?.text || '');
    }
  }
  function segCardOf(seg: { callId?: string } | null | undefined): AgentToolCard | null {
    try {
      const cid = String(seg?.callId || '');
      if (!cid) return null;
      const t = turn.activeTurn;
      const idx = (t.toolCardIndex as Record<string, number>)[cid];
      if (idx !== undefined) return t.toolCards[idx] || null;
      const live = t.toolCards.find((c) => c.callId === cid);
      if (live) return live;
      // P02-G2 毕业只换态不搬家：live 索引摘牌后，用已落盘 tool 消息合成终态卡，轮内原位保留。
      try {
        const hit = persistedMessages.value.find((x) => {
          const r = x as unknown as { toolCallId?: string; id?: string };
          return String(r.toolCallId || '') === cid || String(r.id || '') === cid;
        });
        if (hit) {
          return toCard(hit, {
            key: `seg-${cid}`,
            assistantId: '',
            reasoning: null,
            content: null,
            toolCalls: [{ id: cid, name: String((hit as unknown as { name?: string }).name || 'tool') }],
            toolMessages: [hit],
            telemetry: null,
            createdAtUtc: '',
          });
        }
      } catch {
        /* fallthrough 占位 */
      }
      // 真正未知（卡未建、包未落）：占位“参数接收中”，不留空洞。
      return {
        callId: cid,
        functionName: 'tool',
        argsJson: '',
        status: 'running',
        resultText: '',
        exitCode: null,
        durationMs: null,
        iteration: 0,
        receivedBytes: 0,
        images: [],
      };
    } catch (e) {
      console.warn('[timeline] seg card failed:', e);
      return null;
    }
  }
  /**
   * M2-4：只对「最后一个 text seg」套平滑。
   * 不能整体套：多个 seg 各自跑一个 rAF 会同时开 N 个循环，且已稳定的历史 seg 被反复触发。
   */
  const tailSegKey = computed<string>(() => {
    const segs = stream.visibleSegs;
    if (!segs.length) return '';
    const tail = segs[segs.length - 1];
    return tail ? String(tail.key || '') : '';
  });
  const tailTextSource = computed<string>(() => {
    const segs = stream.visibleSegs;
    if (!segs.length) return '';
    const tail = segs[segs.length - 1];
    return tail && tail.kind === 'text' ? String(tail.text || '') : '';
  });
  // active 必须传：TurnCompleted 后 isGenerating=false → flushAll() 立即追平，
  // 否则平滑仍在跑，会与 finishTurn 清空 segs 竞争，导致最后一段文字被截断。
  const smoothTailText = useSmoothText(tailTextSource, {
    active: computed(() => stream.isGenerating),
    key: tailSegKey,
  });
  // 内嵌流态（含已落盘段，供 UserTurnBlock 内嵌使用）。
  const liveInclusiveSegs = computed<DisplaySeg[]>(() => {
    let segs: Array<{ key?: unknown; kind?: unknown; text?: unknown }> = [];
    try {
      segs = (turn.activeTurn?.segs || []) as Array<{ key?: unknown; kind?: unknown; text?: unknown }>;
    } catch {
      segs = [];
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
        text: isTailText && tailSegKey.value === key ? smoothTailText.value : segTextOf(s as Parameters<typeof segTextOf>[0]),
        card: kind === 'tool' ? segCardOf(s as Parameters<typeof segCardOf>[0]) : null,
      };
    });
  });
  const toolCount = computed(() => cards.toolCards.length);
  const curIteration = computed(() => turn.activeTurn.curIteration || 1);
  // need23-R6：本轮（当前 iteration）工具数；toolCards 跨轮不清空，直接取 length 会误导。
  const curIterToolCount = computed(() => {
    const it = turn.activeTurn.curIteration || 1;
    return cards.toolCards.filter((c) => (c.iteration || 0) === it).length;
  });
  const showLiveTelemetry = computed(() => {
    const t = telemetry.telemetry;
    return t.ttftMs !== null || t.totalMs !== null || t.inputTokens > 0 || t.outputTokens > 0;
  });
  const liveCachePercent = computed(() => {
    const t = telemetry.telemetry;
    return t.inputTokens <= 0 ? '-' : `${Math.round((t.cachedTokens / t.inputTokens) * 100)}%`;
  });
  async function onMarkdownLinkClick(e: MouseEvent): Promise<void> {
    const el = e.target as HTMLElement | null;
    const anchor = el?.closest?.('a[href]') as HTMLAnchorElement | null;
    if (!anchor) return;
    const href = (anchor.getAttribute('href') || '').trim();
    if (!href || href.startsWith('#')) return;
    e.preventDefault();
    e.stopPropagation();
    try {
      const res = await getHostBridge().openExternalLink(href, workspaceStore.activeWorkspace?.rootPath || '');
      if (!res.ok) console.warn('[markdown-link] open failed:', res.code, res.message);
    } catch (err) {
      console.warn('[markdown-link] open failed:', err);
    }
  }
  async function onRetryTurn(): Promise<void> {
    clearReadingWindow();
    if (paneVisible.value) follow.onSendRisingEdge(() => pin.forceToBottom());
    try {
      await turn.retryLastTurn();
    } catch (err) {
      console.warn('[timeline] retry failed:', err);
    }
  }
  function onDismissError(): void {
    try {
      turn.clearLastError();
    } catch {
      /* ignore */
    }
  }
  function attachContainer(el: HTMLElement | null): void {
    scrollEl.value = el;
    try {
      nav.setContainer(el);
      nav.setOrder(timelineItems.value.map((i) => i.key));
    } catch (e) {
      console.warn('[timeline] nav init failed:', e);
    }
    pin.bindUserScroll();
    bindScrollWindow();
    ensureMountWindow();
  }
  function detachContainer(): void {
    pin.unbindUserScroll();
    unbindScrollWindow();
    pin.cancelScheduled();
    try {
      nav.clear();
      nav.setContainer(null);
    } catch (e) {
      console.warn('[timeline] nav clear failed:', e);
    }
    scrollEl.value = null;
  }
  // 切会话守卫：切换后 timelineItems 0→N 的一次性暴涨不触发自动跟随，消费一次即清。
  let sessionSwitchGuard = false;
  watch(
    () => workspaceStore.activeSessionId,
    () => {
      menu.closeMenu();
      clearReadingWindow();
      // 切会话不再自动滑到底：停顶部 + 亮小药丸，用户手动点 pill 回到底部。
      sessionSwitchGuard = true;
      follow.releaseFollow();
      resetMountWindow();
      if (scrollEl.value) scrollEl.value.scrollTop = 0;
    }
  );
  watch(
    () => timelineItems.value.length,
    () => {
      if (sessionSwitchGuard) {
        // 切会话后的异步加载（0→N 暴涨）：按新数据重算窗口并保持顶部，不跟随到底。
        sessionSwitchGuard = false;
        resetMountWindow();
        if (scrollEl.value) scrollEl.value.scrollTop = 0;
        return;
      }
      if (!paneVisible.value || !follow.pinnedRef.value) return;
      void nextTick(() => pin.scheduleToBottom());
    }
  );
  // need23-R3 / M2-3：活动容器内文本增长不改变 timelineItems.length，但会撑高内容。
  // 直接调 pin.scheduleToBottom()（内部 rAF 双帧 + 200ms 兜底，自带帧节流），
  // 不再叠 250ms 定时器 —— 叠了会把窗口期内的增长全部丢弃，变成「每 250ms 猛拽一次」。
  watch(
    () => (stream.streamingText?.length || 0) + (stream.streamingReasoning?.length || 0),
    (len, prev) => {
      if (!len || (prev !== undefined && len <= prev)) return;
      if (!paneVisible.value || !follow.pinnedRef.value) return;
      if (readingWindow.value) return;
      pin.scheduleToBottom();
    }
  );
  // M3-3：轮末活动容器消失（高度一次性塌陷）后补一次跟随，
  // 让「淡出 + 视口归位」连续发生，而不是内容突然上跳。
  watch(
    () => stream.hasActiveTurn,
    (v, prev) => {
      if (prev && !v && paneVisible.value && follow.pinnedRef.value) pin.scheduleToBottom();
    }
  );
  watch(
    () => timelineItems.value.map((i) => i.key),
    (keys) => {
      try {
        nav.setOrder(keys);
      } catch (e) {
        console.warn('[timeline] nav order failed:', e);
      }
    },
    { immediate: true }
  );
  onMounted(() => {
    if (paneVisible.value && follow.pinnedRef.value) void nextTick(() => pin.scheduleToBottom());
    menu.bindMenuGlobalClose();
    try {
      nav.setOrder(timelineItems.value.map((i) => i.key));
    } catch (e) {
      console.warn('[timeline] nav init failed:', e);
    }
  });
  onUnmounted(() => {
    detachContainer();
    // M2-3：250ms 尾随定时器已删除（改由 pin.scheduleToBottom 的 rAF 双帧自节流），
    // 这里只需确保未决的调度被取消。
    pin.cancelScheduled();
  });
  return {
    chatWidthMode,
    persistedMessages,
    timelineItems,
    historyEntries,
    tailEntries,
    visibleHistoryEntries,
    hiddenAbove,
    resetMountWindow,
    ensureMountWindow,
    growWindowTop,
    handleTopReach,
    anchorPrepend,
    bindScrollWindow,
    unbindScrollWindow,
    onTranscriptScroll,
    pinnedRef: follow.pinnedRef,
    showJump: follow.showJump,
    releaseFollow: follow.releaseFollow,
    jumpToLatest: (onReturnToLatest?: () => void) => follow.jumpToLatest(() => pin.forceToBottom(), onReturnToLatest ?? clearReadingWindow),
    notifySend: () => {
      clearReadingWindow();
      if (paneVisible.value) follow.onSendRisingEdge(() => pin.forceToBottom());
    },
    paneVisible,
    setPaneVisible,
    readingWindow,
    setReadingWindow,
    clearReadingWindow,
    liveInclusiveSegs,
    liveSegsFor: (entry: TimelineEntry, segs: DisplaySeg[]): DisplaySeg[] => {
      try {
        if (!isLiveOwner(entry, timelineItems.value)) return [];
        return Array.isArray(segs) ? segs : [];
      } catch {
        return [];
      }
    },
    transcriptRunning,
    isTurnExpanded,
    toggleTurn,
    smoothTailText,
    need02Flags,
    need02Attr,
    liveCompleting: computed(() => turn.activeTurn?.liveCompleting === true),
    handoffAnim: computed(() => need02Flags.p3),
    noticeInTurn: computed(() => need02Flags.p4),
    isRunExpanded,
    toggleRun,
    runByKey,
    setAnchor,
    toolKeyOf,
    toCard,
    msgImages,
    isPendingMessage,
    pendingStatusOf,
    hasLostImage,
    isImageLost,
    thumbOf,
    hasFailedTool,
    failedToolCount,
    cachePercentOf,
    hasTelemetryGroup,
    protocolOf,
    cacheLineOf,
    toolCount,
    curIteration,
    curIterToolCount,
    showLiveTelemetry,
    liveCachePercent,
    onMarkdownLinkClick,
    onRetryTurn,
    onDismissError,
    attachContainer,
    detachContainer,
    menu,
    turn,
    stream,
    cards,
    telemetry,
    nav,
  };
}
export type Timeline = ReturnType<typeof useTimeline>;
