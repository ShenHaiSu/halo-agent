<template>
  <div class="space-y-2 min-w-0" :class="phaseClass">
    <div v-if="phaseLabel" class="flex items-center gap-1.5 text-[11px] text-secondary select-none" :class="{ active: isActive }">
      <Loader2 v-if="isActive" class="w-3 h-3 animate-spin shrink-0" />
      <span>{{ phaseLabel }}</span>
      <span
        v-if="(errorCount ?? 0) > 0"
        class="shrink-0 font-mono text-[10px] px-1.5 py-px rounded-full bg-rose-500/20 text-rose-400 border border-rose-500/40"
        >{{ errorCount }} 个错误</span
      >
      <span v-if="toolTotal > 0" class="shrink-0 font-mono text-[10px] px-1.5 py-px rounded-full border border-subtle"
        >{{ toolTotal }} 个工具</span
      >
    </div>
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
          <!-- 失败指示：1~2 个直接画红点；3 个及以上「红点 ×N」。静态终态，不做呼吸动画。 -->
          <span
            v-if="failedDots > 0"
            class="shrink-0 flex items-center gap-0.5"
            :title="failedTitle"
            :aria-label="failedTitle"
            data-testid="turn-failed-dots"
          >
            <template v-if="failedDots <= 2">
              <span v-for="i in failedDots" :key="i" class="w-1.5 h-1.5 rounded-full bg-rose-500" data-testid="turn-failed-dot" />
            </template>
            <template v-else>
              <span class="w-1.5 h-1.5 rounded-full bg-rose-500" data-testid="turn-failed-dot" />
              <span class="font-mono text-[10px] leading-none text-rose-500" data-testid="turn-failed-dot-count">×{{ failedDots }}</span>
            </template>
          </span>
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
import { computed } from 'vue';
import { BrainCircuit, ChevronDown, Loader2 } from 'lucide-vue-next';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import { turnPhaseOf, turnDurationLabel, type TurnPhase } from '@features/session/utils/turnProcess';
import ToolCallCard from './ToolCallCard.vue';

const props = defineProps<{
  group: TurnGroup;
  expanded: boolean;
  hasFailed: boolean;
  /** 失败 tool 条数；未提供时回落到 hasFailed 的布尔语义（1 个红点）。 */
  failedCount?: number;
  showTelemetry: boolean;
  protocol: string;
  cacheLine: string;
  renderHtml: (text: string) => string;
  cardFor: (m: UnifiedChatMessage) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
  isActive?: boolean;
  stopped?: boolean;
  startedAt?: number | null;
  errorCount?: number;
}>();
const emit = defineEmits<{
  toggle: [key: string];
  'content-menu': [e: MouseEvent];
  'link-click': [e: MouseEvent];
}>();

const phase = computed<TurnPhase>(() =>
  turnPhaseOf({
    hasOwnError: props.hasFailed,
    stopped: !!props.stopped,
    isActive: !!props.isActive,
    hasContent: !!props.group.content,
  })
);
const phaseLabel = computed(() => {
  const dur = turnDurationLabel(props.startedAt ?? null);
  const suffix = dur ? ` · ${dur}` : '';
  const heads: Record<TurnPhase, string> = {
    failed: `失败${suffix}`,
    stopped: `已停止${suffix}`,
    running: `运行中…${suffix}`,
    completed: `已完成${suffix}`,
  };
  return heads[phase.value];
});
const phaseClass = computed(() => (phase.value === 'running' || props.isActive ? 'turn-running' : ''));
const toolTotal = computed(() => props.group.toolMessages?.length ?? 0);
/** 需要点亮的红点数量：优先用精确失败条数，缺省时布尔回落为 1。 */
const failedDots = computed(() => {
  const n = props.failedCount;
  if (typeof n === 'number') return Math.max(0, n);
  return props.hasFailed ? 1 : 0;
});
const failedTitle = computed(() => (failedDots.value > 0 ? `${failedDots.value} 个工具调用失败` : ''));
</script>
<style scoped>
.turn-running {
  position: relative;
}
</style>
