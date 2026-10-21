import { computed, type Ref } from 'vue';
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
        telemetry: {
          inputTokens: pendingAssistant.inputTokens,
          outputTokens: pendingAssistant.outputTokens,
          cachedTokens: pendingAssistant.cachedTokens,
        },
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
      else
        items.push({
          key: m.id || `t_${items.length}`,
          kind: 'turn',
          msg: m,
          group: { key: m.id, assistantId: '', content: m.content || '', toolMessages: [m] },
        });
    } else if (role === 'user' || role === 'system') {
      flush();
      items.push({ key: m.id || `${role}_${items.length}`, kind: role as 'user' | 'system', msg: m });
    }
  }
  flush();
  return items;
}
