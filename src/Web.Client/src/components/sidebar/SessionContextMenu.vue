<!-- src/Web.Client/src/components/sidebar/SessionContextMenu.vue -->
<script setup lang="ts">
import HMenu from '@/components/base/HMenu.vue';
import HMenuItem from '@/components/base/HMenuItem.vue';

export type SessionMenuKey = 'rename' | 'copy-id' | 'delete';

defineProps<{ open: boolean }>();
const emit = defineEmits<{
  'update:open': [value: boolean];
  select: [key: SessionMenuKey];
}>();

function pick(key: SessionMenuKey): void {
  emit('select', key);
  emit('update:open', false);
}
</script>

<template>
   <HMenu :open="open" align="end" @update:open="$emit('update:open', $event)">
    <template #trigger>
      <slot name="trigger" />
    </template>
    <HMenuItem icon="edit" @click="pick('rename')">重命名</HMenuItem>
    <HMenuItem icon="copy" @click="pick('copy-id')">复制会话 ID</HMenuItem>
    <HMenuItem icon="trash" danger @click="pick('delete')">删除</HMenuItem>
  </HMenu>
</template>
