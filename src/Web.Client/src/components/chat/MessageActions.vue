<!-- src/Web.Client/src/components/chat/MessageActions.vue -->
<!-- P5 D5-3 消息操作栏：复制 / 重新生成（仅 assistant）。悬停可见，icon-only 按钮带 label。 -->
<script setup lang="ts">
import HIconButton from '@/components/base/HIconButton.vue';
import { useToast } from '@/composables/useToast';
import { copyText } from '@/utils/clipboard';
import type { MessageItem } from '@/stores/messages';

const props = defineProps<{ message: MessageItem }>();
defineEmits<{ retry: [] }>();

const toast = useToast();

async function onCopy(): Promise<void> {
  const ok = await copyText(props.message.content);
  if (ok) toast.success('已复制');
  else toast.error('复制失败');
}
</script>

<template>
  <div class="actions">
    <HIconButton icon="copy" label="复制内容" size="sm" @click="onCopy" />
    <HIconButton
      v-if="message.role === 'assistant'"
      icon="refresh"
      label="重新生成"
      size="sm"
      @click="$emit('retry')"
    />
  </div>
</template>

<style scoped>
.actions {
  display: flex;
  gap: 2px;
  opacity: 0;
  transition: opacity 120ms;
}
.msg-item:hover .actions,
.actions:focus-within {
  opacity: 1;
}
</style>
