<template>
  <section v-if="flatTurns.length > 0 || pending" ref="rootRef" class="timeline-summary-section" data-testid="timeline-summary-section">
    <PendingBubble
      v-if="pending"
      :pending="pending"
      @user-menu="emit('user-menu', $event, pendingKey)"
      @retry-pending="emit('retry-pending')"
    />
    <TimelineProcessBox
      v-for="turn in flatTurns"
      :key="turn.key"
      :turn="turn"
      @expand-change="onExpandChange"
      @user-menu="(ev, id) => emit('user-menu', ev, id)"
      @assistant-menu="(ev, id) => emit('assistant-menu', ev, id)"
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
}>();

const emit = defineEmits<{
  (e: 'user-menu', ev: MouseEvent, turnId: string): void;
  (e: 'assistant-menu', ev: MouseEvent, turnId: string): void;
  (e: 'retry-pending'): void;
}>();

const nav = useTimelineNav();
const rootRef = ref<HTMLElement | null>(null);
const flatTurns = computed(() => flatBlocks(props.summaries));
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
watch(flatTurns, () => {
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
