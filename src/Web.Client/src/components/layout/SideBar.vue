<!-- src/Web.Client/src/components/layout/SideBar.vue -->
<script setup lang="ts">
import SessionList from '@/components/sidebar/SessionList.vue';
import { openSettings } from '@/components/settings/SettingsApp.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

/**
 * 侧栏：会话列表（P5 完整实现：虚拟滚动 + 分组 + 搜索 + 重命名 + 删除）。
 */
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <div class="min-h-0 flex-1">
      <SessionList />
    </div>
    <div class="shrink-0 border-t border-border-subtle p-2">
      <div role="group" aria-label="底部操作" class="flex gap-2">
        <button
          type="button"
          class="flex w-1/2 items-center justify-center gap-2 rounded-md px-2 py-1.5 text-sm text-fg-muted transition-colors duration-fast hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
          @click="openSettings('usage')"
        >
          <HaloIcon name="chart" :size="14" /><span class="truncate">用量</span>
        </button>
        <button
          type="button"
          class="flex w-1/2 items-center justify-center gap-2 rounded-md px-2 py-1.5 text-sm text-fg-muted transition-colors duration-fast hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
          @click="openSettings('general')"
        >
          <HaloIcon name="settings" :size="14" /><span class="truncate">设置</span>
        </button>
      </div>
    </div>
  </div>
</template>
