<template>
  <div v-if="pending" class="flex justify-end" data-testid="pending-bubble">
    <UserBubble
      :content="String(pending.content ?? '')"
      :pending-status="String(pending.status ?? 'sending')"
      @user-menu="emit('user-menu', $event)"
      @retry-pending="emit('retry-pending')"
    />
  </div>
</template>

<script setup lang="ts">
import type { PendingUser } from '../utils/pendingUser';
import UserBubble from '@features/agent/components/userturn/UserBubble.vue';

/** 摘要区头部乐观气泡：直渲染 pending，不再伪装成消息过投影。 */
defineProps<{ pending: PendingUser | null }>();

const emit = defineEmits<{
  'user-menu': [e: MouseEvent];
  'retry-pending': [];
}>();
</script>
