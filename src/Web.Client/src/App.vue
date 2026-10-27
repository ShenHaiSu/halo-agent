<!-- src/Web.Client/src/App.vue -->
<!-- D4-10 应用根：DEV + #/__gallery 时渲染组件预览，否则渲染主界面骨架。 -->
<script setup lang="ts">
import { defineAsyncComponent, onMounted, onBeforeUnmount, ref } from 'vue';
import { transport } from '@/transport';
import { useEventPump } from '@/stores/useEventPump';
import { useHostStore } from '@/stores/host';
import { useSessionsStore } from '@/stores/sessions';
import { useSessionStore } from '@/stores/session';
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import { useUiStore } from '@/stores/ui';
import { useThemeStore } from '@/stores/theme';
import { usePermissionsStore } from '@/stores/permissions';

/**
 * ============ 预览路由（A-9.6：不进生产产物） ============
 *  `import.meta.env.DEV` 在生产构建时被替换为 false，
 *  Rollup 会把整个死分支（含下面的动态 import）消除，
 *  所以生产产物里不含预览代码（06 §11.6 用 grep 验证）。
 * ============ 主界面骨架 ============
 *  D4-6 的 AppShell / TitleBar / SideBar 落地后，本文件内联的
 *  header/aside/main 将替换为 `<AppShell>`；D4-5 的 HToast 落地后，
 *  右下角的 toast 容器替换为 `<HToast>`。store 与事件链路不需要动。
 * ============ ============
 */

// DEV-only 预览（生产构建时整个分支被消除，产物不含画廊字符串）。
// 注意：所有画廊相关（import 路径、hash、监听）都在 import.meta.env.DEV 死分支内，
// Rollup 在生产构建时整体消除；切勿把 hash 字面量提到分支外（会被常量折叠进产物）。
let PreviewView: ReturnType<typeof defineAsyncComponent> | null = null;
const isDev = import.meta.env.DEV;
const isPreview = ref(false);
if (import.meta.env.DEV) {
  PreviewView = defineAsyncComponent(() => import('./components/__gallery/GalleryApp.vue'));
  const GH = '#/__' + 'gallery';
  const checkHash = (): boolean => {
    try {
      return window.location.hash === GH;
    } catch {
      return false;
    }
  };
  isPreview.value = checkHash();
  const onHashChange = (): void => {
    isPreview.value = checkHash();
  };
  window.addEventListener('hashchange', onHashChange);
  onBeforeUnmount(() => window.removeEventListener('hashchange', onHashChange));
}

// ---- 主界面状态 ----
const host = useHostStore();
const sessions = useSessionsStore();
const session = useSessionStore();
const messages = useMessagesStore();
const stream = useStreamStore();
const ui = useUiStore();
const themeStore = useThemeStore();
const permissions = usePermissionsStore();

const isMock = transport.isMock;
const promptText = ref('');
const promptBusy = ref(false);

// 事件拉取循环（挂载时启动，卸载时停止）
useEventPump();

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

function toggleTheme() {
  const next =
    themeStore.mode === 'midnight' ? 'aurora' : themeStore.mode === 'aurora' ? 'dark' : 'midnight';
  themeStore.setMode(next);
}

async function sendPrompt() {
  const text = promptText.value.trim();
  if (!text || promptBusy.value || stream.busy) return;
  promptBusy.value = true;
  try {
    messages.append({ role: 'user', content: text });
    promptText.value = '';
    await transport.request('agent.prompt', { text });
    // assistant 回复经 useEventPump → dispatchFrames 流式进入 messages
  } catch (e) {
    ui.toast.error(`发送失败：${e instanceof Error ? e.message : String(e)}`);
  } finally {
    promptBusy.value = false;
  }
}

function goGallery(): void {
  if (import.meta.env.DEV) window.location.hash = '#/__' + 'gallery';
}

function toastLevelCls(level: string): string {
  switch (level) {
    case 'success':
      return 'border-success/40 bg-success-subtle text-success';
    case 'warning':
      return 'border-warning/40 bg-warning-subtle text-warning';
    case 'error':
      return 'border-danger/40 bg-danger-subtle text-danger';
    default:
      return 'border-border bg-bg-elevated text-fg';
  }
}
</script>

<template>
  <!-- DEV 预览 -->
  <Suspense v-if="isPreview && PreviewView">
    <component :is="PreviewView" />
  </Suspense>

  <!-- 主界面骨架 -->
  <div v-else class="flex h-full w-full flex-col overflow-hidden bg-bg text-fg">
    <!-- 标题栏（D4-6 TitleBar 落地后替换；MOCK 标识保留） -->
    <header
      class="flex h-10 shrink-0 items-center gap-3 border-b border-border bg-bg-subtle px-3"
      style="-webkit-app-region: drag"
    >
      <span class="h-2.5 w-2.5 shrink-0 rounded-full bg-accent" aria-hidden="true" />
      <span class="text-sm font-medium">HaloAgent</span>
      <span
        v-if="host.engineStatus.state !== 'ready'"
        class="rounded bg-bg-muted px-1.5 py-0.5 text-2xs text-fg-muted"
        :title="host.engineStatus.reason ?? host.engineStatus.state"
      >
        引擎{{ host.engineStatus.state === 'starting' ? '启动中' : host.engineStatus.state }}
      </span>
      <span
        v-if="isMock"
        data-mock-badge
        class="rounded bg-warning-subtle px-1.5 py-0.5 text-2xs font-medium text-warning"
        title="当前使用 Mock 数据，没有连接真实引擎"
        >MOCK</span
      >
      <div class="flex-1" />
      <span class="hidden text-2xs text-fg-subtle sm:inline">{{
        session.cwd || host.systemInfo?.cwd || ''
      }}</span>
      <button
        type="button"
        class="rounded border border-border px-2 py-0.5 text-xs text-fg-muted hover:text-fg"
        style="-webkit-app-region: no-drag"
        @click="toggleTheme"
      >
        主题：{{ themeStore.themeId }}
      </button>
      <button
        v-if="isDev"
        type="button"
        class="rounded border border-border px-2 py-0.5 text-xs text-fg-muted hover:text-fg"
        style="-webkit-app-region: no-drag"
        title="打开组件预览"
        @click="goGallery"
      >
        组件预览
      </button>
    </header>

    <div class="flex min-h-0 flex-1">
      <!-- 侧栏（D4-6 SideBar 落地后替换） -->
      <aside
        v-if="ui.sidebarVisible"
        class="flex w-60 shrink-0 flex-col border-r border-border bg-bg-subtle"
      >
        <div class="flex items-center justify-between px-3 py-2">
          <span class="text-xs font-medium text-fg-muted">会话</span>
          <button
            type="button"
            class="rounded bg-accent px-2 py-0.5 text-xs font-medium text-accent-fg hover:bg-accent-hover"
            @click="sessions.createNew()"
          >
            新建
          </button>
        </div>
        <nav class="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
          <p v-if="sessions.loading" class="px-2 py-1 text-xs text-fg-subtle">加载中…</p>
          <p v-else-if="sessions.list.length === 0" class="px-2 py-1 text-xs text-fg-subtle">
            暂无会话
          </p>
          <button
            v-for="s in sessions.list"
            :key="s.id"
            type="button"
            class="mb-0.5 block w-full rounded-md px-2 py-1.5 text-left transition-colors"
            :class="
              sessions.currentId === s.id
                ? 'bg-accent-subtle text-accent'
                : 'text-fg-muted hover:bg-bg-muted hover:text-fg'
            "
            @click="sessions.select(s.id)"
          >
            <span class="block truncate text-sm">{{ s.title }}</span>
            <span class="block font-mono text-2xs opacity-70">{{
              sessions.relativeTimeOf(s)
            }}</span>
          </button>
        </nav>
      </aside>

      <!-- 主区（D4-6 MainPane 落地后替换；P5 接入 Markdown/工具卡片渲染） -->
      <main class="flex min-w-0 flex-1 flex-col">
        <div class="min-h-0 flex-1 overflow-y-auto p-4">
          <div class="mx-auto w-full max-w-3xl space-y-3">
            <p v-if="messages.loading" class="text-sm text-fg-subtle">加载快照中…</p>
            <p
              v-else-if="messages.isEmpty"
              class="rounded-lg border border-border bg-bg-subtle p-6 text-center text-sm text-fg-muted"
            >
              还没有消息。在下方输入框提问，Mock 引擎会流式回答。
            </p>
            <article
              v-for="m in messages.items"
              :key="m.id"
              class="rounded-lg border px-3 py-2"
              :class="
                m.role === 'user' ? 'border-border bg-bg-subtle' : 'border-border-subtle bg-bg'
              "
            >
              <header class="mb-1 flex items-center gap-2">
                <span class="text-2xs font-medium text-fg-subtle">{{
                  m.role === 'user' ? '你' : '助手'
                }}</span>
                <span
                  v-if="m.streaming"
                  class="inline-block h-2 w-2 animate-pulse rounded-full bg-accent"
                  aria-label="生成中"
                />
                <span v-if="m.truncatedByUser" class="text-2xs text-warning">（已中断）</span>
              </header>
              <p class="whitespace-pre-wrap break-words text-sm leading-relaxed">{{ m.content }}</p>
            </article>
          </div>
        </div>

        <!-- 待答询问（P5 权限 UI 落地后替换） -->
        <div
          v-if="permissions.hasPending"
          class="border-t border-warning/40 bg-warning-subtle px-4 py-2"
        >
          <p class="text-sm font-medium text-warning">{{ permissions.pending?.title }}</p>
          <p class="mt-0.5 whitespace-pre-wrap text-xs text-fg">{{ permissions.pending?.body }}</p>
          <p v-if="permissions.pending?.warning" class="mt-0.5 text-xs font-medium text-danger">
            {{ permissions.pending?.warning }}
          </p>
          <div class="mt-2 flex gap-2">
            <button
              type="button"
              class="rounded bg-accent px-3 py-1 text-xs font-medium text-accent-fg"
              @click="permissions.allow()"
            >
              允许
            </button>
            <button
              v-if="permissions.canAlways"
              type="button"
              class="rounded border border-border px-3 py-1 text-xs"
              @click="permissions.allowAlways()"
            >
              总是允许
            </button>
            <button
              type="button"
              class="rounded border border-border px-3 py-1 text-xs"
              @click="permissions.deny()"
            >
              拒绝
            </button>
          </div>
        </div>

        <!-- 输入区占位（P5 输入框落地后替换） -->
        <footer class="shrink-0 border-t border-border bg-bg-subtle p-3">
          <form class="mx-auto flex w-full max-w-3xl gap-2" @submit.prevent="sendPrompt">
            <input
              v-model="promptText"
              class="min-w-0 flex-1 rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-fg placeholder:text-fg-subtle focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
              placeholder="输入问题，回车发送（P4 占位输入框）…"
              :disabled="stream.busy"
            />
            <button
              type="submit"
              class="shrink-0 rounded-md bg-accent px-4 py-1.5 text-sm font-medium text-accent-fg hover:bg-accent-hover disabled:opacity-50"
              :disabled="!promptText.trim() || stream.busy"
            >
              {{ stream.busy ? '生成中…' : '发送' }}
            </button>
          </form>
        </footer>
      </main>
    </div>

    <!-- toast 容器（D4-5 HToast 落地后替换为 <HToast>） -->
    <div
      class="pointer-events-none fixed bottom-4 right-4 z-toast flex w-80 flex-col gap-2"
      role="status"
      aria-live="polite"
    >
      <div
        v-for="t in ui.toast.items"
        :key="t.id"
        class="pointer-events-auto flex items-start gap-2 rounded-lg border px-3 py-2 text-sm shadow-lg"
        :class="toastLevelCls(t.level)"
      >
        <span class="min-w-0 flex-1 break-words">{{ t.text }}</span>
        <button
          v-if="t.action"
          type="button"
          class="shrink-0 rounded border border-current px-1.5 py-0.5 text-xs font-medium"
          @click="
            t.action.onClick();
            ui.toast.dismiss(t.id);
          "
        >
          {{ t.action.label }}
        </button>
        <button
          type="button"
          class="shrink-0 text-xs opacity-60 hover:opacity-100"
          aria-label="关闭通知"
          @click="ui.toast.dismiss(t.id)"
        >
          ×
        </button>
      </div>
    </div>
  </div>
</template>
