<!-- src/Web.Client/src/components/sidebar/NewSessionButton.vue -->
<script setup lang="ts">
import { useSessionsStore } from '@/stores/sessions';
import { useToast } from '@/composables/useToast';
import HButton from '@/components/base/HButton.vue';

const sessions = useSessionsStore();
const toast = useToast();

async function create(): Promise<void> {
  try {
    await sessions.createNew();
  } catch (e) {
    // store 内已对 state.busy toast，调用方不再重复。
    if (String((e as Error)?.message ?? '').includes('有回合正在执行')) return;
    toast.error(`新建会话失败：${(e as Error).message}`);
  }
}
</script>

<template>
  <div class="new-wrap">
    <HButton
      variant="primary"
      size="sm"
      block
      icon="message-plus"
      aria-label="新建对话"
      @click="create"
    >
      新建对话
    </HButton>
  </div>
</template>

<style scoped>
 .new-wrap {
   padding: var(--space-1) var(--space-2) var(--space-2);
 }
</style>
