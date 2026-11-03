<!-- src/Web.Client/src/components/input/ContextWaterline.vue -->
<!-- P23：上下文水位线（输入框右上角）。横条 0–100%，默认只展示百分比，hover/focus 切 used/total。 -->
<script setup lang="ts">
import { computed, ref, toRef } from 'vue';
import { useSessionStore } from '@/stores/session';
import { useContextWaterline } from '@/composables/useContextWaterline';

const session = useSessionStore();

const wl = useContextWaterline({
  lastPromptTokens: toRef(session, 'lastPromptTokens'),
  estimatedInitialTokens: toRef(session, 'estimatedInitialTokens'),
  contextWindow: toRef(session, 'contextWindow'),
});

const hovering = ref(false);
const focused = ref(false);
const showHover = computed(() => hovering.value || focused.value);
const text = computed(() => (showHover.value ? wl.hoverText.value : wl.label.value));
const title = computed(() =>
  wl.overflowHint.value ? `${wl.hoverText.value}（${wl.overflowHint.value}）` : wl.hoverText.value,
);
</script>

<template>
  <span
    v-if="wl.visible.value"
    class="waterline"
    :class="[`tone-${wl.tone.value}`, { 'is-overflow': wl.state.value === 'overflow' }]"
    tabindex="0"
    role="progressbar"
    :aria-valuenow="wl.pct.value ?? 0"
    aria-valuemin="0"
    aria-valuemax="100"
    :title="title"
    @mouseenter="hovering = true"
    @mouseleave="hovering = false"
    @focusin="focused = true"
    @focusout="focused = false"
  >
    <span class="wbar" aria-hidden="true">
      <span class="wfill" :style="{ width: `${wl.barPct.value}%` }" />
    </span>
    <span class="wtext font-mono">{{ text }}</span>
    <span v-if="wl.isEstimated.value" class="west">预估</span>
  </span>
</template>

<style scoped>
.waterline {
  display: inline-flex;
  align-items: center;
  gap: var(--space-1);
  cursor: default;
  outline: none;
}
.waterline:focus-visible {
  box-shadow: var(--shadow-focus);
  border-radius: var(--radius-sm);
}
.wbar {
  display: inline-block;
  width: 72px;
  height: 5px;
  border-radius: 999px;
  background: hsl(var(--border));
  overflow: hidden;
}
.wfill {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: hsl(var(--accent));
  transition: width 0.2s ease;
}
.tone-warn .wfill {
  background: hsl(var(--warning));
}
.tone-danger .wfill {
  background: hsl(var(--danger));
}
.is-overflow .wbar {
  animation: w-pulse 1.2s ease-in-out infinite;
}
@keyframes w-pulse {
  50% {
    opacity: 0.55;
  }
}
.wtext {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  min-width: 3em;
  text-align: right;
}
.west {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  opacity: 0.75;
}
</style>
