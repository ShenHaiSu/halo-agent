<!-- need02-P3：轮末交接容器（LiveBody 与 FoldedSummary 交叉淡入淡出）。
     只用 opacity/translate（D4=A）；高度零动画（05 R6）；leave 必须 absolute 脱流。
     p3=off（animOn=false）或 prefers-reduced-motion 时零 transition、直接切换（内容仍正确交接）。 -->
<template>
  <div
    class="uturn-handoff"
    :data-handoff="animOn !== false ? 'on' : 'off'"
    :data-completing="completing ? 'true' : undefined"
    data-testid="user-turn-transition"
  >
    <div v-if="showLive" class="uturn-handoff-live" :class="isFading ? 'uturn-live-fading' : ''" data-testid="user-turn-handoff-live">
      <slot name="live" />
    </div>
    <div class="uturn-handoff-steady" :class="isFading ? 'uturn-steady-arriving' : ''" data-testid="user-turn-handoff-steady">
      <slot />
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{
  /** 本轮是否仍在流（turn.isRunning）。 */
  running?: boolean;
  /** 是否处于轮末 300ms 交接窗（model.liveCompleting，reloadNow 已就位、流容器未清）。 */
  completing?: boolean;
  /** P3 开关（false = P2 硬切：直接切换，无动画）。readingWindow 期调用方直接传 false（暂停交接动画）。 */
  animOn?: boolean;
}>();

/** 动画总开关（p3；false = P2 硬切：交接窗内直接切终态，不共存叠加）。缺省开。 */
const animOnEff = computed(() => props.animOn !== false);
/** 流容器可见性：运行中全程可见；交接窗内仅动画开时与终态共存做交叉淡出（P3 编排）。 */
const showLive = computed(() => !!props.running || (!!props.completing && animOnEff.value));
/** 淡入淡出类：仅 completing + 运行已熄 + 动画开（CSS 另以 [data-handoff=on] 双保险）。 */
const isFading = computed(() => !!props.completing && !props.running && animOnEff.value);
</script>
<style scoped>
.uturn-handoff {
  position: relative;
  min-width: 0;
}
/* 交接窗：live 脱流覆盖在稳态之上淡出，稳态同步淡入；两者只动 opacity/translate。 */
.uturn-handoff[data-handoff='on'][data-completing='true'] .uturn-handoff-live.uturn-live-fading {
  position: absolute;
  left: 0;
  right: 0;
  top: 0;
  z-index: 1;
  pointer-events: none;
  animation: uturn-live-out 0.14s ease forwards;
}
.uturn-handoff[data-handoff='on'][data-completing='true'] .uturn-handoff-steady.uturn-steady-arriving {
  animation: uturn-steady-in 0.14s ease;
}
@keyframes uturn-live-out {
  from {
    opacity: 1;
  }
  to {
    opacity: 0;
  }
}
@keyframes uturn-steady-in {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
  to {
    opacity: 1;
    transform: none;
  }
}
@media (prefers-reduced-motion: reduce) {
  .uturn-handoff .uturn-handoff-live,
  .uturn-handoff .uturn-handoff-steady {
    animation: none !important;
  }
}
</style>
