<template>
  <section v-if="flatTurns.length > 0 || pending" ref="rootRef" class="timeline-summary-section" data-testid="timeline-summary-section">
    <TimelineProcessBox
      v-for="turn in flatTurns"
      :key="turn.key"
      :turn="turn"
      :hide-final="handoffActive && turn.key === lastTurnKey"
      :initial-markdown="handoffActive && turn.key === lastTurnKey ? handoffFinalText : ''"
      @expand-change="onExpandChange"
      @user-menu="(ev, id) => emit('user-menu', ev, id)"
      @assistant-menu="(ev, id) => emit('assistant-menu', ev, id)"
    />
    <PendingBubble
      v-if="pending"
      :pending="pending"
      @user-menu="emit('user-menu', $event, pendingKey)"
      @retry-pending="emit('retry-pending')"
    />
  </section>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { useTimelineNav } from '@features/agent/composables/useTimelineNav';
import TimelineProcessBox from './TimelineProcessBox.vue';
import PendingBubble from './PendingBubble.vue';
import type { PendingUser } from '../utils/pendingUser';
import { setTurnExpanded } from '../utils/timelineExpansion';
import { flatBlocks, summaryAnchorKeys } from '../utils/timelineSummary';

const props = defineProps<{
  summaries: TurnSummary[];
  pending?: PendingUser | null;
  /** 交接窗激活：隐藏刚完成轮在摘要区的正文，正文唯一实例由 Live 交接容器承担。 */
  handoffActive?: boolean;
  /** 交接期 Live 正文文本，喂给稳态盒作首帧 markdown 种子。 */
  handoffFinalText?: string;
}>();

const emit = defineEmits<{
  (e: 'user-menu', ev: MouseEvent, turnId: string): void;
  (e: 'assistant-menu', ev: MouseEvent, turnId: string): void;
  (e: 'retry-pending'): void;
}>();

const nav = useTimelineNav();
const rootRef = ref<HTMLElement | null>(null);
const flatTurns = computed(() => flatBlocks(props.summaries));
// 交接窗里刚完成轮必是摘要最后一条（pendingUser 已在 finishTurn 清理），
// 只对这一条隐藏正文；历史轮正文照常显示。
const lastTurnKey = computed(() => flatTurns.value[flatTurns.value.length - 1]?.key ?? '');
const pendingKey = computed(() => String(props.pending?.requestId || props.pending?.key || 'pending'));
const registered = ref<string[]>([]);

function onExpandChange(turnId: string, expanded: boolean): void {
  try {
    setTurnExpanded(turnId, expanded);
  } catch {
    /* quota counter never blocks render */
  }
}

function releaseAnchors(): void {
  for (const key of registered.value) {
    try {
      nav.unregister(key);
    } catch {
      /* 注册表释放失败不影响卸载 */
    }
  }
  registered.value = [];
}

function selectorEscape(value: string): string {
  return String(value).replace(/["\\]/g, (c) => `\\${c}`);
}

function collectAnchors(): void {
  releaseAnchors();
  const root = rootRef.value;
  if (!root) return;
  const keys: string[] = [];
  for (const turn of flatTurns.value) {
    const anchors = summaryAnchorKeys(turn.turnId, turn.finalBlockId);
    const safeId = selectorEscape(turn.turnId);
    const turnEl = root.querySelector(`[data-turn-key="${safeId}"]`);
    if (turnEl instanceof HTMLElement) {
      nav.register(anchors.user, turnEl);
      keys.push(anchors.user);
    }
    if ((turn as { kind?: string }).kind === 'compact') continue; // P007：桩只注册 user 锚点
    const processEl = root.querySelector(`[data-testid="timeline-process-head-${safeId}"]`);
    if (processEl instanceof HTMLElement) {
      nav.register(anchors.process, processEl);
      keys.push(anchors.process);
    }
    const finalEl =
      root.querySelector(`[data-testid="timeline-final-${safeId}"]`) ??
      root.querySelector(`[data-testid="timeline-final-empty-${safeId}"]`);
    if (finalEl instanceof HTMLElement) {
      nav.register(anchors.final, finalEl);
      keys.push(anchors.final);
    }
  }
  registered.value = keys;
}

onMounted(() => {
  void nextTick(collectAnchors);
});
watch([flatTurns, () => props.handoffActive], () => {
  void nextTick(collectAnchors);
});
onUnmounted(releaseAnchors);
</script>

<style scoped>
.timeline-summary-section {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 0;
}
/* caption 已下线：摘要区顶部不再展示标题行 */
</style>
