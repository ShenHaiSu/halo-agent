<!-- src/Web.Client/src/components/common/EngineStatusBar.vue -->
<!-- 引擎状态条（05 §9.1）：状态圆点（ready 绿 / restarting 黄 / failed 红）+ resync 按钮。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import { useHostStore } from '@/stores/host';
import { hostBridge } from '@/bridge/host';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import HSpinner from '@/components/base/HSpinner.vue';
import HButton from '@/components/base/HButton.vue';

const host = useHostStore();
const toast = useToast();

const resyncing = ref(false);
const state = computed(() => host.engineStatus.state);

/** 状态 → 圆点颜色：ready 绿 / starting+restarting 黄 / failed+stopped 红。 */
const dotClass = computed(() => {
  switch (state.value) {
    case 'ready':
      return 'dot-green';
    case 'starting':
    case 'restarting':
      return 'dot-yellow';
    default:
      return 'dot-red';
  }
});

const text = computed(() => {
  switch (state.value) {
    case 'starting':
      return '正在启动后台服务…';
    case 'restarting':
      return '后台服务异常，正在重启…';
    case 'failed':
      return '后台服务无法启动';
    case 'stopped':
      return '后台服务已停止';
    default:
      return '后台服务运行中';
  }
});

const spinning = computed(() => state.value === 'starting' || state.value === 'restarting');

async function resync(): Promise<void> {
  if (resyncing.value) return;
  resyncing.value = true;
  try {
    await transport.request('agent.resync', null);
    toast.success('已重新同步');
  } catch (e) {
    toast.error(`同步失败：${(e as Error).message}`);
  } finally {
    resyncing.value = false;
  }
}

async function retryEngine(): Promise<void> {
  try {
    await hostBridge.restartEngine();
    toast.info('正在重启后台服务…');
  } catch (e) {
    toast.error(`重启失败：${(e as Error).message}`);
  }
}
</script>

<template>
  <div class="engine-bar" role="status" aria-live="polite">
    <span class="dot" :class="dotClass" aria-hidden="true" />
    <HSpinner v-if="spinning" size="xs" />
    <span class="eb-text">{{ text }}</span>
    <span v-if="host.engineStatus.reason" class="eb-reason" :title="host.engineStatus.reason">
      {{ host.engineStatus.reason }}
    </span>
    <span class="spacer" />
    <HButton size="sm" variant="ghost" :loading="resyncing" @click="resync">重新同步</HButton>
    <HButton
      v-if="state === 'failed' || state === 'stopped'"
      size="sm"
      variant="secondary"
      @click="retryEngine"
      >重试</HButton
    >
  </div>
</template>

<style scoped>
.engine-bar {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-3);
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  flex: none;
  min-width: 0;
}
.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: none;
}
.dot-green {
  background: hsl(var(--success));
}
.dot-yellow {
  background: hsl(var(--warning));
}
.dot-red {
  background: hsl(var(--danger));
}
.eb-text {
  white-space: nowrap;
}
.eb-reason {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: hsl(var(--fg-subtle));
}
.spacer {
  flex: 1;
  min-width: 0;
}
</style>
