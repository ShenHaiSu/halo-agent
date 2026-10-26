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
import { buildTurnRuns, type TurnRun } from '@features/session/utils/turnRun';
import type { AgentToolCard } from '@features/agent/stores/model';
import { createBottomPin, PIN_TOLERANCE_PX, useFollowMode, useTimelineNav } from './useTimelineNav';
import { useSmoothText } from './useSmoothText';
import { syncTranscriptProjection } from '@features/session/utils/transcriptCache';
import { INITIAL_MOUNT, isNearTop, STEP, WINDOW_MIN } from '@features/session/utils/transcriptWindow';
import { useTimelineMenu } from './useTimelineMenu';
import { need02Attr as buildNeed02Attr, useNeed02Flags } from '@features/need02/utils/need02Flags';
import { pendingPreviewOf } from '@features/session/utils/pendingUser';
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
   * need02-P0：总开关矩阵快照（只读透传，零行为变更）。
   * P1–P4 的行为分支在各自阶段实现；本阶段仅把开关快照与 `data-need02` 透传串
   * 交给模板（TimelineView 根节点 `data-need02`），供截图对比与调试面板使用。
   */
  const need02Flags = useNeed02Flags();
  const need02Attr = computed<string>(() => buildNeed02Attr(need02Flags));
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
    if (turnRunKillSwitch.value) return raw;
    // P3-A：本轮仍在跑时把「正在执行的最后一步」当作活动锚点，并入同一个舱。
    const activeStepId = turn.activeTurn?.isGenerating ? lastPersistedAssistantId.value : '';
    return buildTurnRuns(raw, { isAssistantActive: runIsActive, activeStepId });
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
      if (prevMsgId === curMsgId && prevGroupKey === curGroupKey) return prev;
    } catch {
      /* fallthrough 重建 */
    }
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
  /**
   * 向上扩窗。返回 true 表示可见窗口真的变大了（调用方需据此做顶部锚定补偿）。
   *
   * 注意：`hiddenAbove` 一旦为 0，`visibleHistoryEntries` 已覆盖全部已加载条目，
   * 此时再往上滚就是真的「到顶」——放手让 handleTopReach 走 loadOlder 分支。
   */
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
    displaySegs,
    /** M2-4：经过逐字平滑的尾段文本（模板/测试可直接读；displaySegs 已包含它）。 */
    smoothTailText,
    transcriptRunning,
    isTurnExpanded,
    toggleTurn,
    /** need-01：无正文轮次合并（kill switch + 汇总舱展开态）。 */
    turnRunKillSwitch,
    /** need02-P0：总开关快照 + data-need02 透传串（只读，零行为变更）。 */
    need02Flags,
    need02Attr,
    isRunExpanded,
    toggleRun,
    runByKey,
    setAnchor,
    toolKeyOf,
    toCard,
    msgImages,
    /** need02-P1：pending 判定/状态（TimelineRow 右下态用）。 */
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
