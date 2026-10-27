<!-- src/Web.Client/src/components/layout/SideBar.vue -->
<script setup lang="ts">
import SessionList from '@/components/sidebar/SessionList.vue';
import { useUiStore } from '@/stores/ui';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 侧栏：会话列表（P5 完整实现：虚拟滚动 + 分组 + 搜索 + 重命名 + 删除）。
 */
const ui = useUiStore();
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <div class="min-h-0 flex-1">
      <SessionList />
    </div>
    <div class="shrink-0 border-t border-border-subtle p-2">
      <button
        type="button"
        class="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-sm text-fg-muted transition-colors duration-fast hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
        @click="ui.openSettings('general')"
      >
        <HaloIcon name="settings" :size="14" />
        设置
      </button>
    </div>
  </div>
</template>
