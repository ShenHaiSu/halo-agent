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
  padding: var(--space-2);
}
</style>
