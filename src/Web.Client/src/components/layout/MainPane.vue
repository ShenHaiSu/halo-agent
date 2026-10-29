<!-- src/Web.Client/src/components/layout/MainPane.vue -->
<script setup lang="ts">
import { ref } from 'vue';
import MessageList from '@/components/chat/MessageList.vue';
import Composer from '@/components/input/Composer.vue';
import NoticeBar from '@/components/chat/NoticeBar.vue';

/**
 * 主区：消息列表 + 通知条 + 输入框（P5 完整实现）。
 */
const composer = ref<InstanceType<typeof Composer> | null>(null);

function fillComposer(text: string): void {
  composer.value?.fill(text);
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <div class="flex min-h-0 flex-1 flex-col">
      <MessageList @use-sample="fillComposer" />
    </div>
    <NoticeBar />
    <Composer ref="composer" />
  </div>
</template>
