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
import { createBottomPin, useTimelineNav } from './useTimelineNav';
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
    void nextTick(() => pin.scheduleToBottom());
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
      pin.forceToBottom();
    }
  );
  watch(
    () => timelineItems.value.length,
    () => {
      void nextTick(() => pin.scheduleToBottom());
    }
  );
  watch(
    () => workspaceStore.loadingMessages,
    (loading) => {
      if (!loading) pin.forceToBottom();
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
    void nextTick(() => pin.scheduleToBottom());
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
