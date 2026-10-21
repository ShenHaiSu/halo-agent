<template>
  <div class="space-y-2 min-w-0">
    <div
      v-if="group.content"
      class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
      @click="emit('link-click', $event)"
      @contextmenu.prevent="emit('content-menu', $event)"
      v-html="renderHtml(group.content || '')"
    />
    <div
      v-if="group.reasoning || (group.toolMessages?.length || 0) > 0"
      class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70 overflow-hidden"
      data-testid="turn-thinking-tools"
    >
      <div class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none" @click="emit('toggle', group.key)">
        <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
          <BrainCircuit class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
          <span class="truncate">🧠 思考与工具调用 · {{ group.toolMessages.length || 0 }} 个工具</span>
          <span
            v-if="hasFailed"
            class="shrink-0 font-mono text-[10px] px-1.5 py-px rounded-full bg-rose-500/20 text-rose-400 border border-rose-500/40"
            >有失败</span
          >
        </span>
        <span class="flex items-center gap-1 text-[11px] text-[var(--reasoning-text)] opacity-80 shrink-0">
          <span>{{ expanded ? '点击折叠' : '点击展开' }}</span>
          <ChevronDown class="w-3 h-3 transition-transform" :class="expanded ? 'rotate-180' : ''" />
        </span>
      </div>
      <n-collapse-transition :show="expanded">
        <div class="px-3 pb-2 space-y-2 max-h-72 overflow-y-auto min-w-0">
          <div v-if="group.reasoning" class="whitespace-pre-wrap break-words select-text app-text-small text-[var(--reasoning-text)]">
            {{ group.reasoning }}
          </div>
          <ToolCallCard v-for="t in group.toolMessages || []" :key="toolKey(t)" :card="cardFor(t)" :default-collapsed="true" />
        </div>
      </n-collapse-transition>
    </div>
    <div
      v-if="showTelemetry"
      class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5"
      title="IN为末轮快照，多轮工具调用轮内多次请求只记末次占用，累计见顶栏Σ"
    >
      <span>协议: {{ protocol }}</span>
      <span>{{ cacheLine }}</span>
      <span>OUT {{ group.telemetry?.outputTokens ?? '-' }}</span>
    </div>
  </div>
</template>
<script setup lang="ts">
import { BrainCircuit, ChevronDown } from 'lucide-vue-next';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import ToolCallCard from './ToolCallCard.vue';

const props = defineProps<{
  group: TurnGroup;
  expanded: boolean;
  hasFailed: boolean;
  showTelemetry: boolean;
  protocol: string;
  cacheLine: string;
  renderHtml: (text: string) => string;
  cardFor: (m: UnifiedChatMessage) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
}>();
const emit = defineEmits<{
  toggle: [key: string];
  'content-menu': [e: MouseEvent];
  'link-click': [e: MouseEvent];
}>();
void props;
</script>
