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
import type { AgentToolCard } from '@features/agent/stores/model';
import { createBottomPin, useFollowMode, useTimelineNav } from './useTimelineNav';
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
  const pin = createBottomPin(() => scrollEl.value, {
    isLocked: () => performance.now() < uiStore.jumpLockUntil,
    release: () => uiStore.releaseJumpLock(),
  });
  const persistedMessages = computed<UnifiedChatMessage[]>(() => sessionMessages.value || []);
  const expandedTurns = reactive<Record<string, boolean>>({});
  const isTurnExpanded = (key: string): boolean => expandedTurns[key] === true;
  const toggleTurn = (key: string): void => {
    expandedTurns[key] = !isTurnExpanded(key);
  };
  const timelineItems = computed<TimelineEntry[]>(() => telemetry.activeTimelineEntries);
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
  // bigChange P2-3: pinned/showJump dual mode; hidden pane never follows/pages/samples.
  const follow = useFollowMode();
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
  const hasFailedTool = (g: TurnGroup): boolean => g.toolMessages.some((m) => String(m.content || '').includes('[Tool Error'));
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
  const displaySegs = computed<DisplaySeg[]>(() =>
    stream.visibleSegs.map((s) => ({ kind: s.kind, key: s.key, text: segTextOf(s), card: s.kind === 'tool' ? segCardOf(s) : null }))
  );
  const toolCount = computed(() => cards.toolCards.length);
  const curIteration = computed(() => turn.activeTurn.curIteration || 1);
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
  onUnmounted(() => detachContainer());
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
    transcriptRunning,
    isTurnExpanded,
    toggleTurn,
    setAnchor,
    toolKeyOf,
    toCard,
    msgImages,
    hasLostImage,
    thumbOf,
    hasFailedTool,
    cachePercentOf,
    hasTelemetryGroup,
    protocolOf,
    cacheLineOf,
    displaySegs,
    toolCount,
    curIteration,
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
