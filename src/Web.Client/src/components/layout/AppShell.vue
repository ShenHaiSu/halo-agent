<!-- src/Web.Client/src/components/layout/AppShell.vue -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import TitleBar from './TitleBar.vue';
import SideBar from './SideBar.vue';
import MainPane from './MainPane.vue';
import DetailPane from './DetailPane.vue';
import HSplitter from '@/components/base/HSplitter.vue';
import HIconButton from '@/components/base/HIconButton.vue';

/**
 * 应用外壳：左栏 + 主区两栏布局，详情为右侧浮层抽屉。
 *
 * ============ 三条关键布局约束 ============
 *  ① 每一层 flex 子项都要 min-width: 0 / min-height: 0
 *     否则内容会撑破容器，出现"整页横向滚动"的经典 bug
 *  ② 滚动只发生在"消息区"，其他区域 overflow: hidden
 *  ③ 左栏 / 抽屉宽度用内联 style（不是 class），因为要实时拖拽
 * ============ ============
 *
 * ============ 右抽屉为何不用 HDrawer ============
 *  HDrawer 是模态的（全屏遮罩 + 焦点陷阱 + body 滚动锁定），
 *  打开后主区不可交互。详情栏是"边看边写"的参考面板
 *  （用量 / 工具 / 撤销 / 上下文 / MCP），必须是 modeless：
 *  无遮罩、不抢焦点、主区可继续输入。所以这里是内容区内的
 *  absolute 浮层，而不是 Teleport 到 body 的模态抽屉。
 * ============ Esc 为何不关闭抽屉 ============
 *  全局 Esc 已保留给"取消生成 / 关闭下拉弹层"（见 useShortcuts），
 *  输入框内的 Esc 也各有归属。抽屉只通过标题栏开关和右上角
 *  关闭按钮控制，避免抢占这些更高优先级的 Esc 语义。
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

/**
 * 右抽屉是浮层，不挤压主区宽度，因此不再按窗口宽度强制隐藏；
 * 窄窗口下抽屉宽度由 drawerWidth / max-width 收窄兜底。
 */
const detailActualVisible = computed(() => layout.value.detailVisible);

/** 窄窗口下收窄抽屉，避免盖住整个主区（CSS max-width 是第二道兜底）。 */
const drawerWidth = computed(() =>
  Math.min(layout.value.detailWidth, Math.max(240, windowWidth.value - 48))
);

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

function closeDetail() {
  layout.value.detailVisible = false;
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

    <div class="relative flex min-h-0 flex-1 overflow-hidden">
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

      <!-- 主区：始终占满侧栏之外的全部宽度，右抽屉不再挤压它 -->
      <main class="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden">
        <MainPane />
      </main>

      <!-- 右抽屉：浮于主区上方的 modeless 浮层，从右侧滑入 -->
      <Transition name="detail-drawer">
        <aside
          v-if="detailActualVisible"
          class="absolute top-0 right-0 bottom-0 z-overlay flex max-w-[min(92vw,640px)] flex-row overflow-hidden border-l border-border bg-bg-subtle shadow-xl"
          :style="{ width: `${drawerWidth}px` }"
          role="complementary"
          aria-label="详情栏"
        >
          <!-- 左边缘拖拽条：调整抽屉宽度 -->
          <HSplitter
            :value="layout.detailWidth"
            :min="280"
            :max="640"
            :default-value="380"
            side="right"
            @update:value="onDetailResize"
            @dragstart="onDragStart"
            @dragend="onDragEnd"
          />

          <div class="flex min-h-0 min-w-0 flex-1 flex-col">
            <div
              class="flex shrink-0 items-center justify-between border-b border-border-subtle py-1 pr-1 pl-3"
            >
              <span class="text-xs font-semibold text-fg-muted">详情</span>
              <HIconButton icon="x" label="关闭详情栏" size="sm" @click="closeDetail" />
            </div>
            <div class="min-h-0 flex-1">
              <DetailPane />
            </div>
          </div>
        </aside>
      </Transition>
    </div>
  </div>
</template>

<style scoped>
.detail-drawer-enter-active,
.detail-drawer-leave-active {
  transition:
    transform var(--duration-slow) var(--ease-out),
    opacity var(--duration-base) var(--ease-out);
}
.detail-drawer-enter-from,
.detail-drawer-leave-to {
  transform: translateX(100%);
  opacity: 0;
}
</style>
