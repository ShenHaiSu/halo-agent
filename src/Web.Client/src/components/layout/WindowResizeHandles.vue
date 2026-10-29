<!-- src/Web.Client/src/components/layout/WindowResizeHandles.vue -->
<!-- P07-Enhance 04：窗体 8 方位缩放热区（B 方案思想：前端 mousedown 同步直调宿主 windowResizeStart）。 -->
<script setup lang="ts">
import { computed } from 'vue';
import { hostBridge } from '@/bridge/host';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const maximized = computed(() => session.windowMaximized);

/**
 * 为何不用 await / 为何同步直调：
 * 宿主 SendMessage(WM_NCLBUTTONDOWN) 必须在左键仍按住时执行，
 * mousedown 处理器里同步直调、不等待，桥的 IPC 延迟才追得上按住窗口期。
 * 最大化下隐藏热区且直接返回（与 WindowDragStart 一致）。
 */
function onResizeStart(hitTest: number) {
  return (e: MouseEvent) => {
    if (e.button !== 0) return;
    if (maximized.value) return;
    e.preventDefault();
    e.stopPropagation();
    void hostBridge.windowResizeStart(hitTest);
  };
}

interface Handle {
  key: string;
  hit: number;
  cls: string;
  cursor: string;
  label: string;
}

const handles: Handle[] = [
  { key: 'l', hit: 10, cls: 'left-0 top-2 bottom-2 w-[8px]', cursor: 'ew-resize', label: '向左缩放' },
  { key: 'r', hit: 11, cls: 'right-0 top-2 bottom-2 w-[8px]', cursor: 'ew-resize', label: '向右缩放' },
  { key: 't', hit: 12, cls: 'top-0 left-2 right-2 h-[6px]', cursor: 'ns-resize', label: '向上缩放' },
  { key: 'tl', hit: 13, cls: 'left-0 top-0 h-[12px] w-[12px]', cursor: 'nwse-resize', label: '向左上缩放' },
  { key: 'tr', hit: 14, cls: 'right-0 top-0 h-[12px] w-[12px]', cursor: 'nesw-resize', label: '向右上缩放' },
  { key: 'b', hit: 15, cls: 'bottom-0 left-2 right-2 h-[8px]', cursor: 'ns-resize', label: '向下缩放' },
  { key: 'bl', hit: 16, cls: 'bottom-0 left-0 h-[12px] w-[12px]', cursor: 'nesw-resize', label: '向左下缩放' },
  { key: 'br', hit: 17, cls: 'bottom-0 right-0 h-[12px] w-[12px]', cursor: 'nwse-resize', label: '向右下缩放' },
];
</script>

<template>
  <div v-if="!maximized" class="pointer-events-none absolute inset-0 z-[1020]" aria-hidden="true">
    <div
      v-for="h in handles"
      :key="h.key"
      class="pointer-events-auto absolute"
      :class="h.cls"
      :style="{ cursor: h.cursor }"
      :aria-label="h.label"
      @mousedown="onResizeStart(h.hit)"
    />
  </div>
</template>
