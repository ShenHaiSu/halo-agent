<!-- src/Web.Client/src/App.vue -->
<!-- P5 应用根：AppShell 三栏 + 设置页 + 命令面板 + 快捷键帮助 + Toast + 错误边界 + 全局快捷键。 -->
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
import { useHostStore } from '@/stores/host';
import { useSessionsStore } from '@/stores/sessions';
import { useSessionStore } from '@/stores/session';
import { useMessagesStore } from '@/stores/messages';
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
const sessions = useSessionsStore();
const session = useSessionStore();
const messages = useMessagesStore();
const stream = useStreamStore();
const ui = useUiStore();
const themeStore = useThemeStore();
void themeStore;

const composing = useComposition();
const toast = useToast();

async function cancelTurn(): Promise<void> {
  try {
    await transport.request('agent.cancel', {});
  } catch (e) {
    toast.error(`取消失败：${(e as Error).message}`);
  }
}

// 事件拉取循环（挂载时启动，卸载时停止）
useEventPump();

// 全局快捷键（capture + IME 守卫 + F5/Ctrl+N 拦截）
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
  void host.load();
  void sessions.load().then(() => {
    if (sessions.list.length > 0 && !sessions.currentId) {
      void sessions.select(sessions.list[0]!.id);
    }
  });
  void messages.loadSnapshot();
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
    <div class="flex h-full w-full flex-col overflow-hidden bg-bg text-fg">
      <div class="min-h-0 flex-1">
        <AppShell />
      </div>
    </div>
    <SettingsApp />
    <CommandPalette />
    <ShortcutHelp />
    <HToast />
    <MockWarningBar />
  </ErrorBoundary>
</template>
