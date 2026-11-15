<template>
  <div
    v-show="bytesRowLatched"
    class="flex items-center gap-1.5 px-2.5 py-1 font-mono text-[11px] text-secondary border-t border-subtle bytes-counter"
    data-testid="tool-bytes-counter"
    :title="bytesTooltip"
  >
    <span class="bytes-dot" :class="bytesDotClass"></span>
    <span class="truncate" style="font-variant-numeric: tabular-nums">{{ bytesText }}</span>
    <span v-if="isReceiving" class="shimmer shrink-0">流式接收中…</span>
    <span v-else-if="card.status === 'running'" class="shrink-0">正在写入磁盘…</span>
    <span v-else-if="card.status === 'done'" class="shrink-0 text-[var(--stat-add-text)]">✓</span>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { AgentToolCard } from '@features/agent/stores/model';
import { formatSmartBytes } from '@shared/utils/formatSmartBytes';

/**
 * 流式写入字节计数行（write/edit 专属）。
 * 单向锁存：该行一旦出现就不再消失，避免反复显隐造成卡片高度抖动。
 */
const props = defineProps<{ card: AgentToolCard }>();

const receivedBytes = computed(() => Math.max(0, Math.floor(props.card.receivedBytes || 0)));
const showBytesRow = computed(() => props.card.functionName === 'edit_file' || props.card.functionName === 'write_file');
const bytesRowLatched = ref(showBytesRow.value);
watch(
  showBytesRow,
  (v) => {
    if (v) bytesRowLatched.value = true;
  },
  { immediate: true }
);
const isReceiving = computed(
  () => props.card.status === 'running' && (props.card.fileBytes === null || props.card.fileBytes === undefined)
);
const bytesText = computed(() => {
  if (props.card.status === 'done' && typeof props.card.fileBytes === 'number' && Number.isFinite(props.card.fileBytes)) {
    return `参数已接收 ${formatSmartBytes(receivedBytes.value)} · 落盘 ${formatSmartBytes(props.card.fileBytes)}`;
  }
  const hot = receivedBytes.value >= 50 * 1024 ? ' 🔥' : '';
  return `参数已接收 ${formatSmartBytes(receivedBytes.value)}${hot}`;
});
const bytesTooltip = computed(() => `参数线字节（含 JSON 转义）≈ ${receivedBytes.value.toLocaleString('en-US')}；文件实际字节以完成态为准`);
const bytesDotClass = computed(() => {
  if (props.card.status === 'done') return 'bytes-dot-done';
  if (props.card.status === 'error') return 'bytes-dot-error';
  return 'bytes-dot-run';
});
</script>

<style scoped>
.bytes-dot {
  width: 7px;
  height: 7px;
  border-radius: 9999px;
  flex-shrink: 0;
}
.bytes-dot-run {
  background: #3b82f6;
  animation: bytes-pulse 1.2s ease-in-out infinite;
}
.bytes-dot-done {
  background: #10b981;
}
.bytes-dot-error {
  background: #ef4444;
}
@keyframes bytes-pulse {
  0%,
  100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.45;
    transform: scale(0.8);
  }
}
.shimmer {
  animation: bytes-pulse 1.2s ease-in-out infinite;
}
.bytes-counter {
  font-variant-numeric: tabular-nums;
  /* 字节行占位高度固定，出现后不再改变卡片总高 */
  min-height: 20px;
}
</style>
