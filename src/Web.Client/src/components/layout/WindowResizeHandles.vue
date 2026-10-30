<!-- src/Web.Client/src/components/layout/WindowResizeHandles.vue -->
<!-- P07-Enhance 04：窗体 8 方位缩放热区（B 方案思想：前端 mousedown 同步直调宿主 windowResizeStart）。 -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue';
import { hostBridge } from '@/bridge/host';
import { useSessionStore } from '@/stores/session';

const session = useSessionStore();
const maximized = computed(() => session.windowMaximized);

/**
 * 缩放热区判定（document 捕获阶段，按视口坐标计算）。
 *
 * 为何不用各热区 div 的 @mousedown：
 * 实机 CEF 109 上，pointer-events 穿透容器内的子 div 能正常命中
 * （命中即改光标）并走完完整捕获→冒泡流程，但 div 上的 Vue 模板
 * 监听器不执行（已用捕获+冒泡双探针实证）。document 捕获监听是经
 * 实证畅通的路径，因此改为坐标判定，一处统一处理。
 *
 * 热区尺寸与下方可视热区一致：四角 12px、四边 8px。
 */
const EDGE = 8;
const CORNER = 12;

function hitFromPoint(x: number, y: number): number | null {
  const w = window.innerWidth;
  const h = window.innerHeight;
  const left = x < EDGE;
  const right = x >= w - EDGE;
  const top = y < EDGE;
  const bottom = y >= h - EDGE;
  if (x < CORNER && y < CORNER) return 13; // HTTOPLEFT
  if (x >= w - CORNER && y < CORNER) return 14; // HTTOPRIGHT
  if (x < CORNER && y >= h - CORNER) return 16; // HTBOTTOMLEFT
  if (x >= w - CORNER && y >= h - CORNER) return 17; // HTBOTTOMRIGHT
  if (top && left) return 13;
  if (top && right) return 14;
  if (bottom && left) return 16;
  if (bottom && right) return 17;
  if (left) return 10; // HTLEFT
  if (right) return 11; // HTRIGHT
  if (top) return 12; // HTTOP
  if (bottom) return 15; // HTBOTTOM
  return null;
}

/**
 * 为何不用 await / 为何同步直调：
 * 宿主 SendMessage(WM_NCLBUTTONDOWN) 必须在左键仍按住时执行，
 * mousedown 处理器里同步直调、不等待，桥的 IPC 延迟才追得上按住窗口期。
 * 最大化下隐藏热区且直接返回（与 WindowDragStart 一致）。
 */
function onDocMouseDown(e: MouseEvent): void {
  if (e.button !== 0) return;
  if (maximized.value) return;
  const hit = hitFromPoint(e.clientX, e.clientY);
  if (hit === null) return;
  e.preventDefault();
  e.stopPropagation();
  void hostBridge.windowResizeStart(hit);
}

onMounted(() => window.addEventListener('mousedown', onDocMouseDown, true));
onBeforeUnmount(() => window.removeEventListener('mousedown', onDocMouseDown, true));

interface Handle {
  key: string;
  cls: string;
  cursor: string;
  label: string;
}

// 纯视觉热区（只负责光标形状，实际触发走上方坐标判定）。
const handles: Handle[] = [
  { key: 'l', cls: 'left-0 top-2 bottom-2 w-[8px]', cursor: 'ew-resize', label: '向左缩放' },
  { key: 'r', cls: 'right-0 top-2 bottom-2 w-[8px]', cursor: 'ew-resize', label: '向右缩放' },
  { key: 't', cls: 'top-0 left-2 right-2 h-[6px]', cursor: 'ns-resize', label: '向上缩放' },
  { key: 'tl', cls: 'left-0 top-0 h-[12px] w-[12px]', cursor: 'nwse-resize', label: '向左上缩放' },
  { key: 'tr', cls: 'right-0 top-0 h-[12px] w-[12px]', cursor: 'nesw-resize', label: '向右上缩放' },
  { key: 'b', cls: 'bottom-0 left-2 right-2 h-[8px]', cursor: 'ns-resize', label: '向下缩放' },
  { key: 'bl', cls: 'bottom-0 left-0 h-[12px] w-[12px]', cursor: 'nesw-resize', label: '向左下缩放' },
  { key: 'br', cls: 'bottom-0 right-0 h-[12px] w-[12px]', cursor: 'nwse-resize', label: '向右下缩放' },
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
    />
  </div>
</template>
