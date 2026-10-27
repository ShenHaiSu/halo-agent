<!-- src/Web.Client/src/components/chat/NoticeBar.vue -->
<!-- P5 通知条：notice（info/success 3 秒自动消失，error/warn 不自动）/
     llm.retrying（"正在重试（attempt/max）：reason"）/
     compactionStarted（spinner + "正在压缩上下文…"）/
     resyncRequired（黄条"界面与后台不同步，正在重新载入…"）。
     说明：stream store 暂无对应字段（不改 stores），以 props 传入，由调用方接线。 -->
<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import HSpinner from '@/components/base/HSpinner.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

export interface Notice {
  text: string;
  level: 'info' | 'success' | 'warning' | 'error';
}

export interface Retrying {
  attempt: number;
  max: number;
  reason: string;
}

const props = withDefaults(
  defineProps<{
    notice?: Notice | null;
    retrying?: Retrying | null;
    compactionStarted?: boolean;
    resyncRequired?: boolean;
  }>(),
  { notice: null, retrying: null, compactionStarted: false, resyncRequired: false }
);

const visibleNotice = ref<Notice | null>(props.notice);
let timer: number | null = null;

watch(
  () => props.notice,
  (n) => {
    visibleNotice.value = n;
    if (timer !== null) {
      window.clearTimeout(timer);
      timer = null;
    }
    // info/success 3 秒自动消失；error/warn 不自动
    if (n && (n.level === 'info' || n.level === 'success')) {
      timer = window.setTimeout(() => {
        visibleNotice.value = null;
        timer = null;
      }, 3000);
    }
  },
  { immediate: true }
);

onBeforeUnmount(() => {
  if (timer !== null) window.clearTimeout(timer);
});
</script>

<template>
  <div class="notice-stack">
    <div v-if="visibleNotice" class="notice" :class="`lv-${visibleNotice.level}`" role="status">
      <HaloIcon
        :name="
          visibleNotice.level === 'success'
            ? 'check-circle'
            : visibleNotice.level === 'error'
              ? 'alert-circle'
              : visibleNotice.level === 'warning'
                ? 'alert'
                : 'info'
        "
        :size="14"
      />
      <span>{{ visibleNotice.text }}</span>
    </div>

    <div v-if="retrying" class="notice lv-info" role="status">
      <HSpinner size="xs" />
      <span>正在重试（{{ retrying.attempt }}/{{ retrying.max }}）：{{ retrying.reason }}</span>
    </div>

    <div v-if="compactionStarted" class="notice lv-info" role="status">
      <HSpinner size="xs" />
      <span>正在压缩上下文…</span>
    </div>

    <div v-if="resyncRequired" class="notice lv-warn" role="alert">
      <HaloIcon name="refresh" :size="14" />
      <span>界面与后台不同步，正在重新载入…</span>
    </div>
  </div>
</template>

<style scoped>
.notice-stack {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  min-width: 0;
}
.notice {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-3);
  font-size: var(--text-xs);
  min-width: 0;
}
.notice > span:last-child {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.lv-info {
  background: hsl(var(--info-subtle));
  color: hsl(var(--info));
}
.lv-success {
  background: hsl(var(--success-subtle));
  color: hsl(var(--success));
}
.lv-warning {
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
}
.lv-error {
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
}
.lv-warn {
  background: hsl(var(--warning-subtle));
  color: hsl(var(--warning));
}
</style>
