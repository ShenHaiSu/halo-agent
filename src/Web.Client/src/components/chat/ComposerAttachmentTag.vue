<template>
  <span
    class="img-tag inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-xs"
    :class="attachment.status === 'ready' ? 'border-subtle bg-elevated' : 'img-tag-invalid'"
    :title="attachment.error || attachment.fileName"
  >
    <img
      v-if="attachment.objectUrl"
      :src="attachment.objectUrl"
      :alt="attachment.fileName"
      class="w-8 h-8 object-cover rounded shrink-0"
      draggable="false"
    />
    <span class="fname max-w-[120px] truncate text-primary">{{ shortName }}</span>
    <n-button
      text
      size="tiny"
      class="shrink-0 hover:text-rose-400"
      :data-testid="`att-remove-${attachment.attId}`"
      :aria-label="`移除图片 ${attachment.fileName}`"
      @click.stop="onRemove"
    >×</n-button>
  </span>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { ComposerAttachment } from '@/utils/composerModel';

const props = defineProps<{
  attachment: ComposerAttachment;
}>();

const emit = defineEmits<{
  (e: 'remove', attId: string): void;
}>();

/** 文件名超 12 字截断显示（完整名走 title） */
const shortName = computed(() => {
  const n = String(props.attachment.fileName || '');
  if ([...n].length <= 12) return n;
  return [...n].slice(0, 12).join('') + '…';
});

function onRemove(): void {
  emit('remove', props.attachment.attId);
}
</script>

<style scoped>
.img-tag-invalid {
  border-color: #f43f5e;
  background: rgba(244, 63, 94, 0.08);
}
</style>
