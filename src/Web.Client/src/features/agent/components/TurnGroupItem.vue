<template>
  <div class="turn-body min-w-0" :class="phaseClass">
    <!-- need-02：状态行已删除（「已完成 · 3s」不再单独占一行）。
         状态词/耗时/失败数全部下沉到折叠栏 header 右侧，正文成为消息块第一个元素，
         间距因此不再随「这一行在不在」在 8px / 16px 之间抖动。 -->
    <div
      v-if="group.content"
      class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0 turn-content"
      @click="emit('link-click', $event)"
      @contextmenu.prevent="emit('content-menu', $event)"
      v-html="renderHtml(group.content || '')"
    />
    <div
      v-if="hasDetail"
      class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 overflow-hidden"
      :class="alertAccent"
      data-testid="turn-thinking-tools"
    >
      <div class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none" @click="emit('toggle', group.key)">
        <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
          <Loader2 v-if="isActive" class="w-3 h-3 animate-spin shrink-0 text-indigo-400" data-testid="turn-phase-spinner" />
          <BrainCircuit v-else class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
          <span class="truncate">🧠 思考与工具调用 · {{ toolTotal }} 个工具</span>
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
          <!-- need-02：状态词/耗时角标（原独立状态行下沉至此，同为 TurnRunItem 的 turn-run-phase 样式）。
               「已完成」静默（opacity 更低）；失败/已停止保持醒目（rose-500）。 -->
          <span
            class="font-mono text-[10px] leading-none"
            :class="phase === 'completed' ? 'opacity-60' : 'text-rose-500 font-semibold'"
            data-testid="turn-phase-badge"
            >{{ phaseLabel }}</span
          >
          <span class="opacity-60">{{ expanded ? '点击折叠' : '点击展开' }}</span>
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
/**
 * need-02：折叠栏是否可展开。与旧的内联 v-if 等价，抽成 computed 是为了让
 * 「状态角标只在有 detail 载体时才存在」这条不变量有单一事实源（无 detail 时
 * phaseLabel 无处安放，属于可接受的静默——简单问答不显示「已完成」）。
 */
const hasDetail = computed(() => !!props.group.reasoning || toolTotal.value > 0);
/**
 * need-02 决策 2-A：失败/已停止用左侧竖线换色做警示，**不额外占一行**。
 * 已完成/运行中维持 indigo 中性色。
 */
const alertAccent = computed(() => (phase.value === 'failed' || phase.value === 'stopped' ? 'border-rose-500' : 'border-indigo-500/70'));
/** 需要点亮的红点数量：优先用精确失败条数，缺省时布尔回落为 1。errorCount 是同一语义的旧入口。 */
const failedDots = computed(() => {
  const n = props.failedCount;
  if (typeof n === 'number') return Math.max(0, n);
  if (typeof props.errorCount === 'number') return Math.max(0, props.errorCount);
  return props.hasFailed ? 1 : 0;
});
const failedTitle = computed(() => (failedDots.value > 0 ? `${failedDots.value} 个工具调用失败` : ''));
</script>
<style scoped>
.turn-running {
  position: relative;
}
</style>
