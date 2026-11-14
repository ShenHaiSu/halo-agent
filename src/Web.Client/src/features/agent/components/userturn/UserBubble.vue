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
    <div v-if="images.length" class="mt-1.5 flex flex-wrap gap-1.5" data-testid="msg-images">
      <template v-for="img in images" :key="img.imageId">
        <n-image
          v-if="thumbOf(img)"
          :src="thumbOf(img)"
          :alt="img.fileName"
          width="96"
          height="96"
          object-fit="cover"
          class="rounded border msg-thumb-slot"
          :data-testid="`msg-image-${img.imageId}`"
          @load="markImgLoaded"
          @error="markImgLoaded"
        />
        <n-tag v-else-if="imgLost(img)" size="small" :bordered="true" :title="img.fileName" class="msg-thumb-slot">
          {{ img.fileName }}
        </n-tag>
        <div v-else class="msg-thumb-slot msg-thumb-skeleton" :title="img.fileName" aria-busy="true" />
      </template>
    </div>
    <div v-if="hasLostImage" class="text-[11px] text-amber-500">部分图片已丢失（文件被删除）</div>
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
import type { ChatImageRef } from '@shared/bridge/hostBridge';
import { userParts } from '@features/agent/utils/userParts';
import SkillPillChip from '@features/agent/components/SkillPillChip.vue';
import { timelineViewFallback, useTimelineViewOptional } from '@features/agent/composables/timelineViewKey';

/** 用户气泡唯一实现：文本分段、图片槽、丢失提示、乐观发送态收敛于此。 */
const props = defineProps<{ content: string; images: ChatImageRef[]; pendingStatus: string }>();

const emit = defineEmits<{ 'user-menu': [e: MouseEvent]; 'retry-pending': [] }>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());

function thumbOf(img: ChatImageRef): string {
  try {
    return view.value.thumbOf(img) || '';
  } catch {
    return '';
  }
}

function imgLost(img: ChatImageRef): boolean {
  try {
    return view.value.imgLost(img);
  } catch {
    return img.lost === true;
  }
}

const hasLostImage = computed(() => {
  try {
    return props.images.some((img) => view.value.imgLost(img));
  } catch {
    return false;
  }
});

const failedCls = computed(() => (props.pendingStatus === 'failed' ? 'border-red-400' : 'border-subtle'));

function markImgLoaded(e: Event): void {
  try {
    const t = e.target as HTMLElement | null;
    t?.setAttribute?.('data-loaded', 'true');
  } catch {
    /* ignore */
  }
}
</script>

<style scoped>
.msg-thumb-slot {
  width: 96px;
  height: 96px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  overflow: hidden;
}
.msg-thumb-skeleton {
  border-radius: 4px;
  border: 1px solid var(--border-subtle);
  background: linear-gradient(90deg, #8881 25%, #8883 50%, #8881 75%);
  background-size: 200% 100%;
  animation: thumb-shimmer 1.2s linear infinite;
}
@keyframes thumb-shimmer {
  to {
    background-position: -200% 0;
  }
}
@media (prefers-reduced-motion: reduce) {
  .msg-thumb-skeleton {
    animation: none;
  }
}
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
