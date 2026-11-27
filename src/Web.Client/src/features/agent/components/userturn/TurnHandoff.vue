<template>
  <!-- 交接期唯一正文容器：user → process → final，正文全程满高不动，只收缩过程段。 -->
  <div class="turn-handoff rounded-lg border border-subtle bg-surface/40 px-3 py-2 min-w-0" data-testid="turn-handoff">
    <div class="turn-handoff-user min-w-0"><slot name="user" /></div>

    <div ref="processRef" class="turn-handoff-process" data-testid="handoff-process-wrapper">
      <slot name="process">
        <StreamSegItem
          v-for="seg in split.process"
          :key="seg.key"
          :seg="seg"
          :stream-md-kill-switch="killSwitch"
          :render-stream="renderStream"
          :is-tail="false"
          :is-generating="false"
        />
      </slot>
    </div>

    <!-- 稳态折叠条在收缩起点浮现：固定 28px，不参与收缩；reduced-motion 下直接显示。 -->
    <div v-if="collapseBarVisible" class="turn-handoff-bar" data-testid="handoff-collapse-bar">
      <span aria-hidden="true">▸</span>
      <span>过程内容</span>
      <span class="opacity-70">点击展开</span>
    </div>

    <div class="turn-handoff-final min-w-0" data-testid="handoff-final">
      <slot name="final">
        <StreamSegItem
          v-for="(seg, i) in split.final"
          :key="seg.key"
          :seg="seg"
          :stream-md-kill-switch="killSwitch"
          :render-stream="renderStream"
          :is-tail="i === split.final.length - 1"
          :is-generating="false"
        />
      </slot>
    </div>
  </div>
</template>

<script setup lang="ts">
/**
 * 流→稳交接容器：把末轮 user / process / final 三槽收进同一个 DOM 容器。
 * 正文（final 槽）全程挂载且满高；只把过程段 wrapper 的实测高度平滑收到 0，
 * 让稳态折叠条在收缩起点浮现，动画结束 emit 换手信号。绝不自己改轮次状态或拉取内容。
 */
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import StreamSegItem from '@features/agent/components/StreamSegItem.vue';
import { splitHandoffSegs } from '@features/agent/utils/handoffGrouping';
import { timelineViewFallback, useTimelineViewOptional, type TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';

const props = defineProps<{
  segs: TimelineLiveSeg[];
  /** 交接窗是否已进入（稳态已就位）。 */
  completing: boolean;
  /** 是否做过程段高度收缩；关闭时容器仅做单份正文编排，不动画。 */
  reduceHeight: boolean;
}>();

const emit = defineEmits<{ 'handoff-done': [] }>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());
const killSwitch = computed(() => view.value.streamMdKillSwitch);
const split = computed(() => splitHandoffSegs(props.segs));

function renderStream(t: string): string {
  try {
    return view.value.renderStream(t);
  } catch {
    return String(t || '');
  }
}

const processRef = ref<HTMLElement | null>(null);
const collapseBarVisible = ref(false);
let collapseStarted = false;
let collapseRunning = false;
let doneTimer = 0;

function finish(): void {
  if (collapseStarted) return;
  collapseStarted = true;
  emit('handoff-done');
}

function prefersReducedMotion(): boolean {
  try {
    return !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  } catch {
    return false;
  }
}

function nextFrame2(): Promise<void> {
  return new Promise<void>((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  });
}

async function collapseProcess(): Promise<void> {
  if (collapseRunning || collapseStarted) return;
  collapseRunning = true;
  const el = processRef.value;
  // 折叠条与收缩同一帧出现（无过程段也要显示稳态折叠条，保持换手前后布局一致）。
  collapseBarVisible.value = true;
  if (!el) {
    finish();
    return;
  }
  if (prefersReducedMotion()) {
    el.style.height = '0px';
    el.style.overflow = 'hidden';
    el.style.opacity = '0';
    finish();
    return;
  }
  // 实测首帧高度：CSS 不能 auto → 0，写死 px 后才归零，保证的是连续变化而非瞬变。
  const h = el.offsetHeight;
  el.style.height = `${h}px`;
  el.style.overflow = 'hidden';
  await nextFrame2();
  el.style.transition = 'height 180ms ease, opacity 140ms ease';
  el.style.height = '0px';
  el.style.opacity = '0';
  doneTimer = window.setTimeout(finish, 190);
}

// 不能 immediate：setup 期 ref 还没挂载，读不到真实 offsetHeight。
// 首帧已满足条件时走 onMounted，之后条件翻转走 watch，两条路径共用同一把 collapseStarted 锁。
onMounted(() => {
  if (props.completing && props.reduceHeight && !collapseStarted) void collapseProcess();
});
watch(
  () => props.completing && props.reduceHeight,
  (active) => {
    if (!active || collapseStarted) return;
    void collapseProcess();
  }
);

onUnmounted(() => {
  if (doneTimer) window.clearTimeout(doneTimer);
});
</script>

<style scoped>
.turn-handoff-process {
  min-width: 0;
}
.turn-handoff-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  margin-top: 4px;
  border-radius: 4px;
  border-left: 2px solid var(--accent-primary, #6366f1);
  background: var(--reasoning-bg, rgba(99, 102, 241, 0.08));
  color: var(--reasoning-text, #a5b4fc);
  font-size: 11px;
  user-select: none;
  animation: handoff-bar-in 160ms ease both;
}
@keyframes handoff-bar-in {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}
@media (prefers-reduced-motion: reduce) {
  .turn-handoff-bar {
    animation: none;
  }
}
</style>
