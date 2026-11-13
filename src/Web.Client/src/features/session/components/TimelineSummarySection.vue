<template>
  <section v-if="flatTurns.length > 0" ref="rootRef" class="timeline-summary-section" data-testid="timeline-summary-section">
    <div class="timeline-summary-caption" data-testid="timeline-summary-caption">
      时间轴摘要 · {{ flatTurns.length }} 轮（后端聚合直显）
    </div>
    <TimelineProcessBox v-for="turn in flatTurns" :key="turn.key" :turn="turn" />
  </section>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import type { TurnSummary } from '@shared/bridge/hostBridge';
import { useTimelineNav } from '@features/agent/composables/useTimelineNav';
import TimelineProcessBox from './TimelineProcessBox.vue';
import { flatBlocks, summaryAnchorKeys } from '../utils/timelineSummary';

const props = defineProps<{
  summaries: TurnSummary[];
}>();

const nav = useTimelineNav();
const rootRef = ref<HTMLElement | null>(null);
const flatTurns = computed(() => flatBlocks(props.summaries));
const registered = ref<string[]>([]);

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
  padding: 12px 16px 4px;
}
.timeline-summary-caption {
  font-size: 11px;
  color: var(--text-secondary);
}
</style>
