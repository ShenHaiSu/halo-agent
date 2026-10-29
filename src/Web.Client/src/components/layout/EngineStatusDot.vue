<!-- src/Web.Client/src/components/layout/EngineStatusDot.vue -->
<!-- P07-Enhance 01：TitleBar 引擎状态指示灯（hover 显示 pid/reason/Mock/版本，失败时可重启）。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import { useHostStore } from '@/stores/host';
import { hostBridge } from '@/bridge/host';
import { transport } from '@/transport';
import { describeEngineStatus, mapEngineState } from './engineStatusMeta';

const host = useHostStore();
const open = ref(false);
const restarting = ref(false);

const meta = computed(() => mapEngineState(host.engineStatus.state));
const titleText = computed(() =>
  describeEngineStatus(host.engineStatus, transport.isMock)
);
const canRestart = computed(
  () => host.engineStatus.state === 'failed' || host.engineStatus.state === 'stopped'
);
const reason = computed(() => host.engineStatus.reason ?? '');
const pid = computed(() => host.engineStatus.pid);
const versions = computed(() => {
  const si = host.systemInfo;
  if (!si) return '';
  return `host ${si.hostVersion} · cef ${si.cefVersion} · ${si.gpuMode}`;
});

async function onRestart(): Promise<void> {
  if (!canRestart.value || restarting.value) return;
  restarting.value = true;
  try {
    await hostBridge.restartEngine();
  } finally {
    restarting.value = false;
  }
}
</script>

<template>
  <div
    class="eng-dot-wrap app-region-no-drag relative flex items-center justify-center"
    role="status"
    :aria-label="`引擎状态：${meta.text}`"
    :title="titleText"
    @mouseenter="open = true"
    @mouseleave="open = false"
    @focusin="open = true"
    @focusout="open = false"
  >
    <button
      type="button"
      class="flex h-7 w-7 items-center justify-center rounded"
      :aria-label="`引擎状态：${meta.text}，查看详情`"
      :disabled="!canRestart"
      :class="canRestart ? 'cursor-pointer' : 'cursor-default'"
      @click="canRestart ? onRestart() : undefined"
    >
      <span class="eng-dot" :class="`st-${host.engineStatus.state}`" aria-hidden="true" />
    </button>
    <div
      v-if="open"
      class="eng-pop absolute right-0 top-full z-[1070] mt-1 w-64 rounded border border-border bg-bg-elevated p-3 shadow-md"
    >
      <div class="text-xs font-medium text-fg">{{ titleText }}</div>
      <div v-if="reason" class="mt-1 text-xs text-fg-muted">原因：{{ reason }}</div>
      <div v-if="typeof pid === 'number'" class="mt-1 text-xs text-fg-muted">PID：{{ pid }}</div>
      <div v-if="transport.isMock" class="mt-1 text-xs text-warning">Mock 模式：未连接真实引擎</div>
      <div v-else-if="versions" class="mt-1 text-xs text-fg-subtle">{{ versions }}</div>
      <button
        v-if="canRestart"
        type="button"
        class="mt-2 rounded bg-accent px-2 py-1 text-xs text-accent-fg disabled:opacity-50"
        :disabled="restarting"
        @click="onRestart"
      >
        {{ restarting ? '重启中…' : '重启引擎' }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.eng-dot {
  width: 8px;
  height: 8px;
  border-radius: 9999px;
  display: inline-block;
}
.st-ready {
  background: hsl(var(--success));
}
.st-starting,
.st-restarting {
  background: hsl(var(--warning));
  animation: eng-pulse 1.6s ease-in-out infinite;
}
.st-failed,
.st-stopped {
  background: hsl(var(--danger));
}
@media (prefers-reduced-motion: reduce) {
  .st-starting,
  .st-restarting {
    animation: none;
  }
}
@keyframes eng-pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.35;
  }
}
</style>
