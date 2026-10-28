<!-- src/Web.Client/src/components/layout/TitleBar.vue -->
<script setup lang="ts">
import { computed } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HIconButton from '@/components/base/HIconButton.vue';
import { hostBridge } from '@/bridge/host';
import { transport } from '@/transport';
import { useSessionStore } from '@/stores/session';

/**
 * 自绘标题栏。
 *
 * ============ 三块区域的职责 ============
 *  左：侧栏开关 + 应用名
 *  中：当前会话标题（可点击重命名）
 *  右：右栏开关 + 最小化/最大化/关闭
 *
 * ============ 与 P3 宿主的分工（B 方案：显式拖拽） ============
 *  - CEF 铺满客户区后 WM_NCHITTEST 到不了窗体，宿主无法靠 hit-test 识别标题栏，
 *    因此拖拽由前端 mousedown 显式发起：调 haloHost.windowDragStart，
 *    宿主 ReleaseCapture + SendMessage(WM_NCLBUTTONDOWN/HTCAPTION) 交给系统
 *    （移动 / 双击 / Aero Snap / Win+方向键与原生标题栏一致）。
 *  - header 上的 app-region-drag 仅保留给 Chromium 的 -webkit-app-region
 *   （页面内行为），真正的窗体移动不依赖它。
 *  - 按钮区标 app-region-no-drag 且 mousedown 时跳过，确保点按钮不变拖窗口。
 *  - 按钮经由 @/bridge/host 调用宿主窗口能力，而不是自己改窗体。
 * ============ 有意例外说明 ============
 *  这里直接 import `@/bridge/host` 的 hostBridge 是有意的例外：
 *  标题栏窗口按钮（最小化/最大化/关闭/拖拽）必须直调宿主桥，
 *  不经过 store。base 组件仍禁止触碰 bridge（见 check-layering）。
 * ============ ============
 */
defineProps<{
  sidebarVisible: boolean;
  detailVisible: boolean;
}>();

const emit = defineEmits<{
  'toggle-sidebar': [];
  'toggle-detail': [];
}>();

const session = useSessionStore();
const isMock = transport.isMock;

const title = computed(() => session.title || 'HaloAgent');

const windowState = computed(() => session.windowMaximized);
const maximizeIcon = computed(() =>
  windowState.value ? ('window-restore' as const) : ('window-maximize' as const)
);

async function onMinimize() {
  await hostBridge.windowMinimize();
}

async function onMaximize() {
  await hostBridge.windowMaximizeOrRestore();
}

async function onClose() {
  await hostBridge.windowClose();
}

/**
 * 标题栏拖拽（B 方案显式入口）。
 *
 * 为何不用 await：宿主 SendMessage(WM_NCLBUTTONDOWN) 必须在左键仍按住时执行，
 * mousedown 处理器里同步直调、不等待，桥的 IPC 延迟才追得上按住窗口期。
 * 为何过滤：右键/中键不拖；双击（detail>1）留给 dblclick 走最大化；
 * 最大化下不拖（原生行为）；落在 .app-region-no-drag（按钮区）的不拖。
 */
function onTitleBarMouseDown(e: MouseEvent) {
  if (e.button !== 0) return;
  if (e.detail > 1) return;
  if (windowState.value) return;
  const t = e.target as HTMLElement | null;
  if (t?.closest?.('.app-region-no-drag')) return;
  void hostBridge.windowDragStart();
}

/** 原生标题栏行为：双击空白区切换最大化/还原（按钮区除外）。 */
function onTitleBarDblClick(e: MouseEvent) {
  const t = e.target as HTMLElement | null;
  if (t?.closest?.('.app-region-no-drag')) return;
  void onMaximize();
}
</script>

<template>
  <header
    class="app-region-drag flex h-titlebar-h shrink-0 items-center gap-1 border-b border-border bg-bg-subtle px-2 select-none"
    @mousedown="onTitleBarMouseDown"
    @dblclick="onTitleBarDblClick"
  >
    <!-- 左：侧栏开关 -->
    <div class="app-region-no-drag flex shrink-0 items-center gap-1">
      <HIconButton
        icon="panel-left"
        label="显示/隐藏侧栏"
        :pressed="sidebarVisible"
        size="md"
        @click="emit('toggle-sidebar')"
      />
    </div>

    <!-- 应用标识 -->
    <div class="flex shrink-0 items-center gap-1.5 px-1">
      <div
        class="flex h-5 w-5 items-center justify-center rounded bg-accent text-accent-fg"
        aria-hidden="true"
      >
        <HaloIcon name="zap" :size="12" :stroke-width="2.5" />
      </div>
      <span class="text-xs font-semibold tracking-wide text-fg-muted">HALO</span>
    </div>

    <!-- 中：会话标题 -->
    <div class="flex min-w-0 flex-1 items-center justify-center gap-2 px-2">
      <span class="u-truncate text-xs text-fg-muted">{{ title }}</span>
      <span
        v-if="isMock"
        class="shrink-0 rounded bg-warning-subtle px-1.5 py-0.5 text-2xs font-medium text-warning"
        title="当前使用 Mock 数据，没有连接真实引擎"
      >
        MOCK
      </span>
    </div>

    <!-- 右：右栏开关 + 窗口按钮 -->
    <div class="app-region-no-drag flex shrink-0 items-center gap-0.5">
      <HIconButton
        icon="panel-right"
        label="显示/隐藏详情栏"
        :pressed="detailVisible"
        size="md"
        @click="emit('toggle-detail')"
      />

      <div class="mx-1 h-4 w-px bg-border" aria-hidden="true" />

      <button
        type="button"
        class="flex h-7 w-9 items-center justify-center rounded text-fg-muted transition-colors duration-fast hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
        aria-label="最小化"
        title="最小化"
        @click="onMinimize"
      >
        <HaloIcon name="window-minimize" :size="13" />
      </button>

      <button
        type="button"
        class="flex h-7 w-9 items-center justify-center rounded text-fg-muted transition-colors duration-fast hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
        :aria-label="windowState ? '还原' : '最大化'"
        :title="windowState ? '还原' : '最大化'"
        @click="onMaximize"
      >
        <HaloIcon :name="maximizeIcon" :size="12" />
      </button>

      <button
        type="button"
        class="flex h-7 w-9 items-center justify-center rounded text-fg-muted transition-colors duration-fast hover:bg-danger hover:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-danger/40"
        aria-label="关闭"
        title="关闭"
        @click="onClose"
      >
        <HaloIcon name="window-close" :size="13" />
      </button>
    </div>
  </header>
</template>
