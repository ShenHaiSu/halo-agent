<!-- src/Web.Client/src/components/chat/MessageAvatar.vue -->
<!-- P5 D5-3 消息头像：按角色显示图标。 -->
<script setup lang="ts">
import { computed } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import type { IconName } from '@/components/icons/paths';

const props = defineProps<{ role: 'user' | 'assistant' | 'tool' | 'system' }>();

const icon = computed<IconName>(() => {
  switch (props.role) {
    case 'user':
      return 'user';
    case 'assistant':
      return 'zap';
    case 'tool':
      return 'terminal';
    default:
      return 'info';
  }
});
</script>

<template>
  <div class="avatar" :class="`av-${role}`" aria-hidden="true">
    <HaloIcon :name="icon" :size="16" />
  </div>
</template>

<style scoped>
.avatar {
  flex: none;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
}
.av-assistant {
  background: hsl(var(--accent-subtle));
  color: hsl(var(--accent));
}
</style>
