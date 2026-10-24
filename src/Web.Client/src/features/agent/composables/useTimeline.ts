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
import { imagesOf, type TimelineEntry, type TurnGroup } from '@features/session/utils/turnGroup';
import { buildTurnRuns, type TurnRun } from '@features/session/utils/turnRun';
import type { AgentToolCard } from '@features/agent/stores/model';
import { createBottomPin, useFollowMode, useTimelineNav } from './useTimelineNav';
import { useSmoothText } from './useSmoothText';
import { syncTranscriptProjection } from '@features/session/utils/transcriptCache';
import { INITIAL_MOUNT, STEP, WINDOW_MIN } from '@features/session/utils/transcriptWindow';
import { useTimelineMenu } from './useTimelineMenu';

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
  const { sessionMessages, budgetStatus } = storeToRefs(workspaceStore);
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
  const persistedMessages = computed<UnifiedChatMessage[]>(() => sessionMessages.value || []);
  const expandedTurns = reactive<Record<string, boolean>>({});
  const isTurnExpanded = (key: string): boolean => expandedTurns[key] === true;
  const toggleTurn = (key: string): void => {
    expandedTurns[key] = !isTurnExpanded(key);
  };
  /**
   * need-01：无正文轮次合并（kill switch：localStorage['halo-turn-run'] = 'off' → 回到改动前行为）。
   * 实现方式照 useAgentStream 的 streamMdKillSwitch（隐私模式下 localStorage 抛错时保持默认开启）。
   */
  const turnRunKillSwitch = ref(false);
  try {
    turnRunKillSwitch.value = String(window.localStorage.getItem('halo-turn-run') || '').toLowerCase() === 'off';
  } catch {
    turnRunKillSwitch.value = false;
  }
  /**
   * need-01：末组是否仍在「活动轨」。
   * 条件 = 本轮仍在生成 && 传入的末组 assistantId 就是最后一条已落盘的 assistant
   * （否则历史里的旧 run 会被误判成运行中，导致它默认展开 + 常亮转圈）。
   */
  const lastPersistedAssistantId = computed<string>(() => {
    const msgs = persistedMessages.value;
    for (let i = msgs.length - 1; i >= 0; i--) {
      const m = msgs[i] as UnifiedChatMessage;
      if (String(m.role || '').toLowerCase() === 'assistant') return String(m.id || '');
    }
    return '';
  });
  const runIsActive = (assistantId: string): boolean => {
    const t = turn.activeTurn;
    if (!t || t.isGenerating !== true) return false;
    if (!assistantId) return true;
    return assistantId === lastPersistedAssistantId.value;
  };
  // 只改这一处计算链：activeTimelineEntries →（可选）buildTurnRuns。
  const timelineItems = computed<TimelineEntry[]>(() => {
    const raw = telemetry.activeTimelineEntries;
    return turnRunKillSwitch.value ? raw : buildTurnRuns(raw, { isAssistantActive: runIsActive });
  });
  /** 合并舱索引（纯派生视图，供展开态默认值与「首次合并补一次跟随」判定使用）。 */
  const runByKey = computed<Map<string, TurnRun>>(() => {
    const m = new Map<string, TurnRun>();
    for (const e of timelineItems.value) if (e.kind === 'turn-run' && e.run) m.set(e.run.key, e.run);
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
  const TAIL_SIZE = 8;
  const entryRefs = new Map<string, TimelineEntry>();
  let projGeneration = 0;
  function reuseRef(e: TimelineEntry): TimelineEntry {
    const prev = entryRefs.get(e.key);
    if (prev && prev.kind === e.kind && prev.msg === e.msg && prev.group === e.group) return prev;
    entryRefs.set(e.key, e);
    return e;
  }
  function pruneRefs(live: TimelineEntry[]): void {
    if (entryRefs.size <= 600) return;
    const keep = new Set(live.map((e) => e.key));
    for (const k of entryRefs.keys()) if (!keep.has(k)) entryRefs.delete(k);
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
  function ensureMountWindow(): void {
    hiddenAbove.value = Math.max(0, timelineItems.value.length - INITIAL_MOUNT);
    requestAnimationFrame(() => {
      if (!follow.pinnedRef.value || !paneVisible.value) return;
      hiddenAbove.value = Math.max(0, timelineItems.value.length - WINDOW_MIN);
      pin.forceToBottom();
    });
  }
  function growWindowTop(step = STEP): boolean {
    if (hiddenAbove.value <= 0) return false;
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
  function toCard(m: UnifiedChatMessage, group: TurnGroup): AgentToolCard {
    const callId = String((m as unknown as { toolCallId?: string }).toolCallId || m.id);
    const ref = group.toolCalls.find((t) => t.id === callId);
    let argsJson = '';
    const rawAssistant = persistedMessages.value.find((x) => String(x.id) === group.assistantId);
    const rawCalls = (
      rawAssistant as unknown as {
        toolCalls?: Array<{
          id?: string;
          callId?: string;
          argumentsJson?: string;
          function?: { arguments?: string; name?: string };
          functionName?: string;
        }>;
      }
    ).toolCalls;
    if (Array.isArray(rawCalls)) {
      const hit = rawCalls.find((c) => String(c.id || c.callId) === callId);
      if (hit) argsJson = String(hit.argumentsJson || hit.function?.arguments || '');
    }
    const content = String(m.content || '');
    return {
      callId,
      functionName: ref?.name || (m.name as string) || 'tool',
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
  const msgImages = (msg: UnifiedChatMessage | undefined): ChatImageRef[] => imagesOf(msg);
  const hasLostImage = (msg: UnifiedChatMessage | undefined): boolean =>
    msgImages(msg).some((i) => lostImageIds.value[i.imageId] || i.lost === true);
  /** M1-4：单图丢失判定（供 TimelineRow 决定渲染标签槽位还是加载骨架）。 */
  const isImageLost = (img: ChatImageRef): boolean => img.lost === true || lostImageIds.value[img.imageId] === true;
  function thumbOf(img: ChatImageRef): string {
    if (thumbnailCache.value[img.imageId] || img.lost === true || lostImageIds.value[img.imageId])
      return thumbnailCache.value[img.imageId] || '';
    if (thumbPending.has(img.imageId)) return '';
    thumbPending.add(img.imageId);
    void (async () => {
      try {
        const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
        const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
        const res = await getHostBridge().getImageThumbnail(ws, sid, img.imageId);
        if (res.ok && typeof res.data === 'string' && res.data) thumbnailCache.value = { ...thumbnailCache.value, [img.imageId]: res.data };
        else lostImageIds.value = { ...lostImageIds.value, [img.imageId]: true };
      } catch {
        lostImageIds.value = { ...lostImageIds.value, [img.imageId]: true };
      } finally {
        thumbPending.delete(img.imageId);
      }
    })();
    return '';
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
      return t.toolCards.find((c) => c.callId === cid) || null;
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
  const displaySegs = computed<DisplaySeg[]>(() => {
    const segs = stream.visibleSegs;
    const lastIdx = segs.length - 1;
    return segs.map((s, i) => {
      const isTailText = i === lastIdx && s.kind === 'text';
      return {
        kind: s.kind,
        key: s.key,
        text: isTailText ? smoothTailText.value : segTextOf(s),
        card: s.kind === 'tool' ? segCardOf(s) : null,
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
  const showThinking = computed(() => !stream.visibleSegs.length && !stream.streamingText && stream.streamingActive && stream.isGenerating);
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
    ensureMountWindow();
  }
  function detachContainer(): void {
    pin.unbindUserScroll();
    pin.cancelScheduled();
    try {
      nav.clear();
      nav.setContainer(null);
    } catch (e) {
      console.warn('[timeline] nav clear failed:', e);
    }
    scrollEl.value = null;
  }
  watch(
    () => workspaceStore.activeSessionId,
    () => {
      menu.closeMenu();
      clearReadingWindow();
      if (!paneVisible.value) return;
      follow.onSendRisingEdge(() => pin.forceToBottom());
      ensureMountWindow();
    }
  );
  watch(
    () => timelineItems.value.length,
    () => {
      if (!paneVisible.value || !follow.pinnedRef.value) return;
      void nextTick(() => pin.scheduleToBottom());
    }
  );
  watch(
    () => workspaceStore.loadingMessages,
    (loading) => {
      if (!loading && paneVisible.value && follow.pinnedRef.value) pin.forceToBottom();
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
  /**
   * need-01（05 陷阱 5）：run 首次形成时总高度会一次性变短，与「轮末容器消失」同类，
   * 补一次跟随即可；复用 pin.scheduleToBottom 的 rAF 双帧 + 200ms 兜底，**不新增定时器**。
   * 判据用「主时间轴出现第一条 turn-run 的 key」——它从空到有时只会触发一次。
   */
  const firstRunKey = computed<string>(() => {
    for (const e of timelineItems.value) if (e.kind === 'turn-run') return e.key;
    return '';
  });
  watch(firstRunKey, (key, prev) => {
    if (!key || key === prev) return;
    if (!paneVisible.value || !follow.pinnedRef.value) return;
    if (readingWindow.value) return;
    pin.scheduleToBottom();
  });
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
    ensureMountWindow,
    growWindowTop,
    handleTopReach,
    anchorPrepend,
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
    displaySegs,
    /** M2-4：经过逐字平滑的尾段文本（模板/测试可直接读；displaySegs 已包含它）。 */
    smoothTailText,
    transcriptRunning,
    isTurnExpanded,
    toggleTurn,
    /** need-01：无正文轮次合并（kill switch + 汇总舱展开态）。 */
    turnRunKillSwitch,
    isRunExpanded,
    toggleRun,
    runByKey,
    setAnchor,
    toolKeyOf,
    toCard,
    msgImages,
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
    showThinking,
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
