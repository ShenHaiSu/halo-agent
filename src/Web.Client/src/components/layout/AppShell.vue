<!-- src/Web.Client/src/components/layout/AppShell.vue -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import TitleBar from './TitleBar.vue';
import SideBar from './SideBar.vue';
import MainPane from './MainPane.vue';
import DetailPane from './DetailPane.vue';
import HSplitter from '@/components/base/HSplitter.vue';

/**
 * 应用外壳：三栏布局。
 *
 * ============ 三条关键布局约束 ============
 *  ① 每一层 flex 子项都要 min-width: 0 / min-height: 0
 *     否则内容会撑破容器，出现"整页横向滚动"的经典 bug
 *  ② 滚动只发生在"消息区"，其他区域 overflow: hidden
 *  ③ 各栏宽度用内联 style（不是 class），因为要实时拖拽
 * ============ ============
 */

const LAYOUT_KEY = 'haloagent.layout';

interface LayoutState {
  sidebarVisible: boolean;
  sidebarWidth: number;
  detailVisible: boolean;
  detailWidth: number;
}

const DEFAULT_LAYOUT: LayoutState = {
  sidebarVisible: true,
  sidebarWidth: 260,
  detailVisible: false,
  detailWidth: 380,
};

function loadLayout(): LayoutState {
  try {
    const raw = localStorage.getItem(LAYOUT_KEY);
    if (!raw) return { ...DEFAULT_LAYOUT };
    const parsed = JSON.parse(raw) as Partial<LayoutState>;
    return {
      sidebarVisible: parsed.sidebarVisible ?? DEFAULT_LAYOUT.sidebarVisible,
      sidebarWidth: clamp(parsed.sidebarWidth ?? DEFAULT_LAYOUT.sidebarWidth, 200, 480),
      detailVisible: parsed.detailVisible ?? DEFAULT_LAYOUT.detailVisible,
      detailWidth: clamp(parsed.detailWidth ?? DEFAULT_LAYOUT.detailWidth, 280, 640),
    };
  } catch {
    return { ...DEFAULT_LAYOUT };
  }
}

function clamp(v: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, v));
}

const layout = ref<LayoutState>(loadLayout());
const dragging = ref(false);
const windowWidth = ref(window.innerWidth);

/** 窄窗口自动折叠 */
const autoCollapsed = computed(() => windowWidth.value < 900);

const sidebarActualVisible = computed(() => layout.value.sidebarVisible && !autoCollapsed.value);
const detailActualVisible = computed(() => layout.value.detailVisible && windowWidth.value >= 700);

watch(
  layout,
  (v) => {
    try {
      localStorage.setItem(LAYOUT_KEY, JSON.stringify(v));
    } catch {
      /* 忽略 */
    }
  },
  { deep: true }
);

let resizeObserver: (() => void) | null = null;

onMounted(() => {
  const onResize = () => {
    windowWidth.value = window.innerWidth;
  };
  window.addEventListener('resize', onResize);
  resizeObserver = () => window.removeEventListener('resize', onResize);
});

onBeforeUnmount(() => {
  resizeObserver?.();
});

function toggleSidebar() {
  layout.value.sidebarVisible = !layout.value.sidebarVisible;
}

function toggleDetail() {
  layout.value.detailVisible = !layout.value.detailVisible;
}

function onSidebarResize(w: number) {
  layout.value.sidebarWidth = clamp(w, 200, 480);
}

function onDetailResize(w: number) {
  layout.value.detailWidth = clamp(w, 280, 640);
}

function onDragStart() {
  dragging.value = true;
  document.body.style.cursor = 'col-resize';
  document.body.style.userSelect = 'none';
}

function onDragEnd() {
  dragging.value = false;
  document.body.style.cursor = '';
  document.body.style.userSelect = '';
}

defineExpose({ toggleSidebar, toggleDetail, layout });
</script>

<template>
  <div class="flex h-full w-full flex-col overflow-hidden bg-bg">
    <TitleBar
      :sidebar-visible="layout.sidebarVisible"
      :detail-visible="layout.detailVisible"
      @toggle-sidebar="toggleSidebar"
      @toggle-detail="toggleDetail"
    />

    <div class="flex min-h-0 flex-1 overflow-hidden">
      <!-- 侧栏 -->
      <aside
        v-if="sidebarActualVisible"
        class="flex min-h-0 shrink-0 flex-col overflow-hidden border-r border-border bg-bg-subtle"
        :class="dragging ? '' : 'transition-[width] duration-base ease-out'"
        :style="{ width: `${layout.sidebarWidth}px` }"
      >
        <SideBar />
      </aside>

      <HSplitter
        v-if="sidebarActualVisible"
        :value="layout.sidebarWidth"
        :min="200"
        :max="480"
        side="left"
        @update:value="onSidebarResize"
        @dragstart="onDragStart"
        @dragend="onDragEnd"
      />

      <!-- 主区 -->
      <main class="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden">
        <MainPane />
      </main>

      <!-- 右栏 -->
      <HSplitter
        v-if="detailActualVisible"
        :value="layout.detailWidth"
        :min="280"
        :max="640"
        side="right"
        @update:value="onDetailResize"
        @dragstart="onDragStart"
        @dragend="onDragEnd"
      />

      <aside
        v-if="detailActualVisible"
        class="flex min-h-0 shrink-0 flex-col overflow-hidden border-l border-border bg-bg-subtle"
        :class="dragging ? '' : 'transition-[width] duration-base ease-out'"
        :style="{ width: `${layout.detailWidth}px` }"
      >
        <DetailPane />
      </aside>
    </div>
  </div>
</template>
