import { computed, onUnmounted, ref, type Ref } from 'vue';
import type { UnifiedChatMessage } from '@/bridge/hostBridge';

export interface TimelineGroup {
  key: string;
  assistantId: string;
  content: string;
  reasoning?: string;
  toolMessages: UnifiedChatMessage[];
  telemetry?: { inputTokens?: number | null; outputTokens?: number | null; cachedTokens?: number | null };
}

export interface TimelineItem {
  key: string;
  kind: 'user' | 'system' | 'turn';
  msg?: UnifiedChatMessage;
  group?: TimelineGroup;
}

export function useTimelineGroups(messages: Ref<UnifiedChatMessage[]> | UnifiedChatMessage[]) {
  const list = computed(() => {
    const arr = Array.isArray(messages) ? messages : messages.value;
    return buildTimelineItems(arr || []);
  });
  return { items: list };
}

export function buildTimelineItems(msgs: UnifiedChatMessage[]): TimelineItem[] {
  const items: TimelineItem[] = [];
  let pendingAssistant: UnifiedChatMessage | null = null;
  let pendingTools: UnifiedChatMessage[] = [];
  const flush = () => {
    if (pendingAssistant) {
      const g: TimelineGroup = {
        key: pendingAssistant.id || `g_${items.length}`,
        assistantId: pendingAssistant.id || '',
        content: pendingAssistant.content || '',
        reasoning: pendingAssistant.reasoningContent || undefined,
        toolMessages: [...pendingTools],
        telemetry: { inputTokens: pendingAssistant.inputTokens, outputTokens: pendingAssistant.outputTokens, cachedTokens: pendingAssistant.cachedTokens },
      };
      items.push({ key: g.key, kind: 'turn', msg: pendingAssistant, group: g });
      pendingAssistant = null;
      pendingTools = [];
    }
  };
  for (const m of msgs) {
    const role = (m.role || '').toLowerCase();
    if (role === 'assistant') {
      flush();
      pendingAssistant = m;
    } else if (role === 'tool') {
      if (pendingAssistant) pendingTools.push(m);
      else items.push({ key: m.id || `t_${items.length}`, kind: 'turn', msg: m, group: { key: m.id, assistantId: '', content: m.content || '', toolMessages: [m] } });
    } else if (role === 'user' || role === 'system') {
      flush();
      items.push({ key: m.id || `${role}_${items.length}`, kind: role as 'user' | 'system', msg: m });
    }
  }
  flush();
  return items;
}

/** need22-P3 G1：虚拟列表窗口——只挂载视口±buffer（默认上下各 5 turn），其余占位。
 * 占位高度用 min-height 估计值，IntersectionObserver 进入视口时校准并扩展窗口。
 * data-testid/row-id/kind 约束由调用方模板保持，本函数只做索引窗口。 */
export const VLIST_BUFFER = 5;
export const VLIST_EST_TURN_HEIGHT = 320;
export const VLIST_FULL_MOUNT_THRESHOLD = 14;

export interface VListWindow {
  start: number;
  end: number;
  topSpacerHeight: number;
  bottomSpacerHeight: number;
}

export function useVirtualTurns<T>(items: Ref<T[]>, opts?: { buffer?: number; estHeight?: number }) {
  const buffer = opts?.buffer ?? VLIST_BUFFER;
  const estHeight = opts?.estHeight ?? VLIST_EST_TURN_HEIGHT;
  // 锚点：默认跟随尾部（新消息）；用户上滚后由占位 IO 把锚点拉到对应位置
  const anchorIdx = ref(-1);
  const measuredHeights = ref<Record<number, number>>({});
  const ioSupported = typeof IntersectionObserver !== 'undefined';

  const total = computed(() => items.value.length);

  const windowRange = computed<VListWindow>(() => {
    const n = total.value;
    if (n <= VLIST_FULL_MOUNT_THRESHOLD) {
      return { start: 0, end: n, topSpacerHeight: 0, bottomSpacerHeight: 0 };
    }
    const anchor = anchorIdx.value < 0 ? n - 1 : Math.max(0, Math.min(n - 1, anchorIdx.value));
    const start = Math.max(0, anchor - buffer);
    const end = Math.min(n, anchor + buffer + 1);
    const h = (i: number): number => measuredHeights.value[i] ?? estHeight;
    let top = 0;
    for (let i = 0; i < start; i++) top += h(i);
    let bottom = 0;
    for (let i = end; i < n; i++) bottom += h(i);
    return { start, end, topSpacerHeight: top, bottomSpacerHeight: bottom };
  });

  const visibleItems = computed(() => {
    const w = windowRange.value;
    return items.value.slice(w.start, w.end).map((item, k) => ({ item, index: w.start + k }));
  });

  /** 占位进入视口时调用：把锚点移到该占位边界并记录实测高度做校准 */
  function onPlaceholderVisible(edge: 'top' | 'bottom', measuredPx?: number): void {
    const w = windowRange.value;
    if (edge === 'top' && w.start > 0) {
      if (measuredPx && measuredPx > 0) {
        const hidden = w.start;
        const per = measuredPx / Math.max(1, hidden);
        for (let i = 0; i < hidden; i++) measuredHeights.value[i] = per;
      }
      anchorIdx.value = Math.max(0, w.start - 1);
    } else if (edge === 'bottom' && w.end < total.value) {
      if (measuredPx && measuredPx > 0) {
        const hidden = total.value - w.end;
        const per = measuredPx / Math.max(1, hidden);
        for (let i = w.end; i < total.value; i++) measuredHeights.value[i] = per;
      }
      anchorIdx.value = Math.min(total.value - 1, w.end);
    }
  }

  /** 记录已挂载行的实测高度（供占位 min-height 校准） */
  function recordHeight(index: number, px: number): void {
    if (px > 0) measuredHeights.value[index] = px;
  }

  function followTail(): void {
    anchorIdx.value = -1;
  }

  function observePlaceholders(topEl: Element | null, bottomEl: Element | null): () => void {
    if (!ioSupported || (!topEl && !bottomEl)) return () => undefined;
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (!e.isIntersecting) continue;
        const edge = (e.target as HTMLElement).dataset['edge'] === 'top' ? 'top' : 'bottom';
        const rect = (e.target as HTMLElement).getBoundingClientRect();
        onPlaceholderVisible(edge, rect.height > 0 ? rect.height : undefined);
      }
    }, { rootMargin: '600px 0px' });
    if (topEl) io.observe(topEl);
    if (bottomEl) io.observe(bottomEl);
    return () => { try { io.disconnect(); } catch (e) { console.warn('[vlist] observer disconnect failed:', e); } };
  }

  onUnmounted(() => { measuredHeights.value = {}; });

  return { anchorIdx, windowRange, visibleItems, total, onPlaceholderVisible, recordHeight, followTail, observePlaceholders, buffer, estHeight };
}
