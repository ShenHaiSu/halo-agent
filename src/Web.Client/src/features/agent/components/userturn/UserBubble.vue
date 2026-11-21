<template>
  <div
    class="max-w-[85%] rounded-lg bg-elevated border px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
    :class="failedCls"
    @contextmenu.prevent="emit('user-menu', $event)"
  >
    <template v-for="(part, pi) in userParts(String(content ?? ''))" :key="pi">
      <span v-if="part.type === 'text'">{{ part.text }}</span>
      <SkillPillChip v-else :scope="part.scope" :name="part.name" class="inline-chip" />
    </template>
    <div
      v-if="pendingStatus === 'sending'"
      data-testid="pending-sending"
      class="mt-1 flex items-center justify-end gap-1 text-[11px] text-secondary select-none"
    >
      <span class="pending-spinner" aria-hidden="true" />
      <span>发送中…</span>
    </div>
    <div
      v-else-if="pendingStatus === 'failed'"
      data-testid="pending-failed"
      class="mt-1 flex items-center justify-end gap-1.5 text-[11px] text-red-500 select-none"
    >
      <span>发送失败·点击重发</span>
      <button type="button" class="pending-retry-btn" data-testid="pending-retry-btn" @click.stop="emit('retry-pending')">重发</button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { userParts } from '@features/agent/utils/userParts';
import SkillPillChip from '@features/agent/components/SkillPillChip.vue';

/** 摘要区乐观气泡：直渲染 pending 文本与发送态，不再走图片懒取。 */
const props = defineProps<{ content: string; pendingStatus: string }>();

const emit = defineEmits<{ 'user-menu': [e: MouseEvent]; 'retry-pending': [] }>();

const failedCls = computed(() => (props.pendingStatus === 'failed' ? 'border-red-400' : 'border-subtle'));
</script>

<style scoped>
.pending-spinner {
  width: 11px;
  height: 11px;
  border-radius: 9999px;
  border: 1.5px solid var(--border-subtle);
  border-top-color: var(--text-secondary);
  animation: pending-spin 0.9s linear infinite;
  display: inline-block;
}
@keyframes pending-spin {
  to {
    transform: rotate(360deg);
  }
}
@media (prefers-reduced-motion: reduce) {
  .pending-spinner {
    animation: none;
  }
}
.pending-retry-btn {
  font-size: 11px;
  line-height: 1;
  padding: 3px 8px;
  border-radius: 9999px;
  border: 1px solid #f87171;
  color: #ef4444;
  background: transparent;
  cursor: pointer;
}
.pending-retry-btn:hover {
  background: #fef2f2;
}
</style>
