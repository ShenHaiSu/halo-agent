<template>
  <div class="space-y-2 min-w-0" :class="phaseClass" data-testid="turn-run-item" :data-run-key="run.key">
    <div class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70 overflow-hidden">
      <div class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none" @click="emit('toggle', run.key)">
        <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
          <BrainCircuit class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
          <Loader2 v-if="isActive" class="w-3 h-3.5 shrink-0 animate-spin text-indigo-400" />
          <span class="truncate" data-testid="turn-run-summary">🧠 执行过程 · {{ summaryText }}</span>
          <!-- 失败指示：沿用 TurnGroupItem 的「1~2 直画 / ≥3 红点 ×N」，数据源换成 run 级求和 -->
          <span
            v-if="run.failedCount > 0"
            class="shrink-0 flex items-center gap-0.5"
            :title="failedTitle"
            :aria-label="failedTitle"
            data-testid="turn-run-failed-dots"
          >
            <template v-if="run.failedCount <= 2">
              <span v-for="i in run.failedCount" :key="i" class="w-1.5 h-1.5 rounded-full bg-rose-500" data-testid="turn-run-failed-dot" />
            </template>
            <template v-else>
              <span class="w-1.5 h-1.5 rounded-full bg-rose-500" data-testid="turn-run-failed-dot" />
              <span class="font-mono text-[10px] leading-none text-rose-500" data-testid="turn-run-failed-dot-count"
                >×{{ run.failedCount }}</span
              >
            </template>
          </span>
          <span class="shrink-0 font-mono text-[10px] opacity-70" data-testid="turn-run-phase">{{ phaseLabel }}</span>
        </span>
        <span class="flex items-center gap-1 text-[11px] text-[var(--reasoning-text)] opacity-80 shrink-0">
          <span>{{ expanded ? '点击折叠' : '点击展开' }}</span>
          <ChevronDown class="w-3 h-3 transition-transform" :class="expanded ? 'rotate-180' : ''" />
        </span>
      </div>
      <!-- 高度绝不做动画：展开/收起统一走 n-collapse-transition（05 陷阱 6） -->
      <n-collapse-transition :show="expanded">
        <div class="px-3 pb-2 max-h-72 overflow-y-auto min-w-0">
          <RunStepList :groups="run.groups" :card-for="cardFor" :tool-key="toolKey" />
        </div>
      </n-collapse-transition>
    </div>
  </div>
</template>
<script setup lang="ts">
/**
 * need-01 P2：无正文轮次汇总舱（外壳沿用 TurnGroupItem 的折叠栏样式，内容区换成按 iteration 分节）。
 *
 * 与 TurnGroupItem 的差异（05 陷阱 10）：状态词**自己实现且只出一次**（run 级），
 * 不复用 TurnGroupItem 的 phaseLabel —— 否则会出现「一个舱里 8 次已完成」。
 * 明细渲染下沉到 RunStepList，供 Phase 3 的活动容器复用。
 */
import { computed } from 'vue';
import { BrainCircuit, ChevronDown, Loader2 } from 'lucide-vue-next';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TurnGroup } from '@features/session/utils/turnGroup';
import type { TurnRun } from '@features/session/utils/turnRun';
import { runPhaseLabel, runSummaryText } from '@features/session/utils/turnRun';
import type { AgentToolCard } from '@features/agent/stores/model';
import RunStepList from './RunStepList.vue';

const props = defineProps<{
  run: TurnRun;
  expanded: boolean;
  /** 用户正停留在历史阅读窗口（不点亮「运行中」呼吸态）。 */
  readingWindow?: boolean;
  cardFor: (m: UnifiedChatMessage, g: TurnGroup) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
}>();
const emit = defineEmits<{ toggle: [key: string] }>();

const summaryText = computed(() => runSummaryText(props.run).replace(/^执行过程 · /, ''));
const isActive = computed(() => props.run.isRunning && !props.readingWindow);
const phaseLabel = computed(() => runPhaseLabel(props.run, { failed: props.run.failedCount > 0, stopped: false }));
const failedTitle = computed(() => `${props.run.failedCount} 个工具调用失败`);
const phaseClass = computed(() => (isActive.value ? 'turn-running' : ''));
</script>
<style scoped>
.turn-running {
  position: relative;
}
</style>
