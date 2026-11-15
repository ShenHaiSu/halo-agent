import { computed } from 'vue';
import type { ComputedRef, Ref } from 'vue';
import type { TurnGroup } from '@features/session/utils/turnGroup';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { toolCallsOf } from '@shared/bridge/hostBridge';
import { imagesOf } from '@features/session/utils/turnGroup';
import { isToolErrorText } from '@features/session/utils/toolErrorText';
import { isP4SubOn } from '@features/need02/utils/need02Flags';
import type { Need02Flags } from '@features/need02/utils/need02Flags';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useAgentCards } from '@features/agent/stores/useAgentCards';
import type { AgentToolCard } from '@features/agent/stores/model';

interface CardsDeps {
  persistedMessages: ComputedRef<UnifiedChatMessage[]> | Ref<UnifiedChatMessage[]>;
  need02Flags: Need02Flags;
  budgetProtocol: ComputedRef<string>;
}

/**
 * 卡片派生：工具卡记忆化 + 失败/遥测派生 + 流段文本/卡片选择器。
 * 缓存只存纯派生结果，不修改传入消息对象。
 */
export function useTimelineCards(deps: CardsDeps) {
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const cards = useAgentCards();

  const toolKeyOf = (m: UnifiedChatMessage): string => String((m as unknown as { toolCallId?: string }).toolCallId || m.id);

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

  function toCardUncached(m: UnifiedChatMessage, group: TurnGroup, callId: string, content: string, fnName: string): AgentToolCard {
    let argsJson = '';
    const rawAssistant = deps.persistedMessages.value.find((x) => String(x.id) === group.assistantId);
    const rawCalls = toolCallsOf(rawAssistant || null);
    if (rawCalls.length > 0) {
      const hit = rawCalls.find((c) => String(c.id) === callId);
      if (hit) argsJson = String(hit.argsJson || '');
    }
    return {
      callId,
      functionName: fnName || (m.name as string) || 'tool',
      argsJson,
      status: isToolErrorText(content) ? 'error' : 'done',
      resultText: content.length > 6000 ? content.slice(0, 6000) + '\n…（输出过长已截断）' : content,
      exitCode: null,
      durationMs: null,
      iteration: 0,
      receivedBytes: 0,
      images: imagesOf(m),
    };
  }

  function toCard(m: UnifiedChatMessage, group: TurnGroup): AgentToolCard {
    const callId = String((m as unknown as { toolCallId?: string }).toolCallId || m.id);
    const content = String(m.content || '');
    const useCache = isP4SubOn('proj', null, deps.need02Flags);
    const ref = group.toolCalls.find((t) => t.id === callId);
    const fnName = ref?.name || (m.name as string) || 'tool';
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

  const failedToolCount = (g: TurnGroup): number =>
    (g.toolMessages || []).reduce((n, m) => n + (String(m.content || '').includes('[Tool Error') ? 1 : 0), 0);
  const hasFailedTool = (g: TurnGroup): boolean => failedToolCount(g) > 0;
  const cachePercentOf = (cached: number, input: number): string => (!input || input <= 0 ? '-' : `${Math.round((cached / input) * 100)}%`);
  const hasTelemetryGroup = (g: TurnGroup): boolean =>
    !!g.telemetry && (g.telemetry.inputTokens != null || g.telemetry.outputTokens != null);
  function protocolOf(g: TurnGroup): string {
    void g;
    return (deps.budgetProtocol.value || 'chat').toLowerCase() === 'resp' ? 'RESP' : 'CHAT';
  }
  function cacheLineOf(g: TurnGroup): string {
    return `IN ${g.telemetry?.inputTokens ?? '-'} · Cache ${g.telemetry?.cachedTokens ?? 0} · ${cachePercentOf(g.telemetry?.cachedTokens ?? 0, g.telemetry?.inputTokens ?? 0)}`;
  }

  function segTextOf(seg: { text?: unknown; key?: unknown; kind?: unknown; callId?: unknown } | null | undefined): string {
    try {
      return stream.segDisplayText(seg as unknown as Parameters<typeof stream.segDisplayText>[0]);
    } catch (e) {
      console.warn('[timeline:cards] seg text failed:', e);
      return String(seg?.text || '');
    }
  }

  function segCardOf(seg: { text?: unknown; key?: unknown; kind?: unknown; callId?: unknown } | null | undefined): AgentToolCard | null {
    try {
      const cid = String(seg?.callId || '');
      if (!cid) return null;
      const t = turn.activeTurn;
      const idx = (t.toolCardIndex as Record<string, number>)[cid];
      if (idx !== undefined) return t.toolCards[idx] || null;
      const live = t.toolCards.find((c) => c.callId === cid);
      if (live) return live;
      try {
        const hit = deps.persistedMessages.value.find((x) => {
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
      } catch (e) {
        console.warn('[timeline:cards] seg persisted fallback failed:', e);
      }
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
      console.warn('[timeline:cards] seg card failed:', e);
      return null;
    }
  }

  const toolCount = computed(() => cards.toolCards.length);
  const curIteration = computed(() => turn.activeTurn.curIteration || 1);
  const curIterToolCount = computed(() => {
    const it = turn.activeTurn.curIteration || 1;
    return cards.toolCards.filter((c) => (c.iteration || 0) === it).length;
  });

  return {
    toolKeyOf,
    toCard,
    toCardUncached,
    failedToolCount,
    hasFailedTool,
    cachePercentOf,
    hasTelemetryGroup,
    protocolOf,
    cacheLineOf,
    segTextOf,
    segCardOf,
    toolCount,
    curIteration,
    curIterToolCount,
  };
}

export type TimelineCards = ReturnType<typeof useTimelineCards>;
