<!-- src/Web.Client/src/App.vue -->
<!-- 应用根：AppShell 两栏 + 右抽屉 + 设置页 + 命令面板 + 快捷键帮助 + Toast + 错误边界 + 全局快捷键。 -->
<script setup lang="ts">
import { defineAsyncComponent, onBeforeUnmount, onMounted } from 'vue';
import AppShell from './components/layout/AppShell.vue';
import SettingsApp from './components/settings/SettingsApp.vue';
import CommandPalette from './components/common/CommandPalette.vue';
import ShortcutHelp, { openShortcutHelp } from './components/common/ShortcutHelp.vue';
import ErrorBoundary from './components/common/ErrorBoundary.vue';
import HToast from './components/base/HToast.vue';
import { transport } from '@/transport';
import { useEventPump } from '@/stores/useEventPump';
import { useToast } from '@/composables/useToast';
import MockWarningBar from './components/common/MockWarningBar.vue';
import BootSplash from './components/common/BootSplash.vue';
import WindowResizeHandles from './components/layout/WindowResizeHandles.vue';
import { useHostStore } from '@/stores/host';
import { useBootStore } from '@/stores/boot';
import { useSessionsStore } from '@/stores/sessions';
import { useSessionStore } from '@/stores/session';
import { useStreamStore } from '@/stores/stream';
import { useUiStore } from '@/stores/ui';
import { useThemeStore } from '@/stores/theme';
import { useShortcuts } from '@/composables/useShortcuts';
import { useComposition } from '@/composables/useComposition';

// DEV-only 预览（生产构建时整个分支被消除，产物不含画廊字符串）。
let PreviewView: ReturnType<typeof defineAsyncComponent> | null = null;
const isDev = import.meta.env.DEV;
let isPreview = false;
if (import.meta.env.DEV) {
  PreviewView = defineAsyncComponent(() => import('./components/__gallery/GalleryApp.vue'));
  const GH = '#/__' + 'gallery';
  try {
    isPreview = window.location.hash === GH;
  } catch {
    /* ignore */
  }
  window.addEventListener('hashchange', () => {
    try {
      if (window.location.hash === GH) window.location.reload();
    } catch {
      /* ignore */
    }
  });
}

// ---- 主界面状态 ----
const host = useHostStore();
const boot = useBootStore();
const sessions = useSessionsStore();
const session = useSessionStore();
const stream = useStreamStore();
const ui = useUiStore();
const themeStore = useThemeStore();
void themeStore;

const composing = useComposition();
const toast = useToast();

async function cancelTurn(): Promise<void> {
  const stream = useStreamStore();
  if (stream.cancelling) return;            // 防抖，同按钮
  stream.beginCancel();                     // ★ 同样给乐观反馈
  try {
    const r = (await transport.request<{ cancelled?: boolean }>('agent.cancel', null)) ?? {};
    if (r.cancelled === false) {
      stream.endCancel();
      stream.busy = false;
    }
  } catch (e) {
    stream.endCancel();
    toast.error(`取消失败：${(e as Error).message}`);
  }
}

// 事件拉取循环（挂载时启动，卸载时停止）
useEventPump();

 // 应用内快捷键（capture + IME 守卫 + 获焦门控；OS 级热键已拆除）
useShortcuts({
  isComposing: () => composing.value as unknown as boolean,
  hasOpenModal: () => ui.settingsOpen || ui.commandPaletteOpen,
  closeModal: () => ui.closeSettings(),
  openSettings: () => ui.openSettings('general'),
  focusSearch: () => ui.toggleCommandPalette(),
  showShortcutHelp: () => openShortcutHelp(),
  newSession: () => void sessions.createNew(),
  isBusy: () => stream.busy,
  cancelTurn: () => void cancelTurn(),
});

onMounted(() => {
  session.restoreMode();
  // 启动门（boot gate）：dist 下前端常比引擎先就绪，直接发请求只会收获
  // 一片“引擎桥接不可用”误报。用纯前端启动动画盖住首屏，等引擎就绪
  // （或超时降级）后再发初始请求，绕开发布初期的竞态报错。
  void boot.waitForEngine().then(() => {
    void host.load();
    // 启动串行化。先加载会话列表并切首个会话（select 内含快照绘制），
    // 删除裸 messages.loadSnapshot()，避免与 select 竞态互相覆盖。
    void sessions
      .load()
      .then(() => {
        if (sessions.list.length > 0 && !sessions.currentId) {
          void sessions.select(sessions.list[0]!.id).catch((e: unknown) => {
            console.warn('[boot] 启动自动切会话失败', e);
          });
        }
      })
      .catch((e: unknown) => {
        // 门后才到这里：引擎超时未就绪等真故障，照常提示（不再是抢跑误报）。
        toast.error(`加载会话列表失败：${(e as Error).message}`);
      });
  });
});

onBeforeUnmount(() => undefined);
</script>

<template>
  <!-- DEV 预览 -->
  <Suspense v-if="isDev && isPreview && PreviewView">
    <component :is="PreviewView" />
  </Suspense>

  <!-- 主界面 -->
  <ErrorBoundary v-else>
    <div class="relative flex h-full w-full flex-col overflow-hidden bg-bg text-fg">
      <WindowResizeHandles />
      <div class="min-h-0 flex-1">
        <AppShell />
      </div>
    </div>
    <BootSplash />
    <SettingsApp />
    <CommandPalette />
    <ShortcutHelp />
    <HToast />
    <MockWarningBar />
  </ErrorBoundary>
</template>
