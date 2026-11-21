<template>
  <KeepAlive :max="3">
    <div v-show="paneActive" :aria-hidden="paneActive ? undefined : 'true'" class="flex-1 min-h-0 min-w-0 flex flex-col">
      <TimelineView
        :is-empty="isEmpty"
        :show-jump-btn="!!t.showJump"
        :reading-window="!!t.readingWindow"
        :chat-width-mode="t.chatWidthMode"
        :need02-attr="t.need02Attr"
        @ready="onReady"
        @user-menu="(e, item) => rowActions.onUserMenu(e, item)"
        @assistant-menu="(e, item) => rowActions.onAssistantMenu(e, item)"
        @link-click="t.onMarkdownLinkClick($event)"
        @retry="t.onRetryTurn()"
        @retry-pending="rowActions.onRetryPending()"
        @dismiss="t.onDismissError()"
        @menu-copy="t.menu.onMenuCopy()"
        @menu-copy-turn="t.menu.onMenuCopyTurn()"
        @menu-replay="t.menu.onMenuReplay()"
        @menu-branch="t.menu.onMenuBranch()"
        @menu-delete="t.menu.onMenuDelete()"
        @jump-latest="t.jumpToLatest()"
      />
    </div>
  </KeepAlive>
</template>

<script setup lang="ts">
import { computed, onUnmounted, reactive, ref } from 'vue';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import TimelineView from './TimelineView.vue';
import { useTimelineViewModel } from '../composables/useTimelineViewModel';
import { renderHtmlSmart, renderStreamingText } from '@features/session/utils/markdownRender';
import { bindCodeCopyDelegation } from '@features/session/utils/codeCopyDelegation';
import { isP4SubOn, useP02Flags } from '@features/need02/utils/need02Flags';
import { provideTimelineScene } from '../composables/useTimelineScene';
import { useTimelineRowActions } from '../composables/useTimelineRowActions';

/** 装配器：组装视图模型并提供，模板只做数据下发与事件转发。 */
const rawTimeline = useTimelineViewModel();
const t = reactive(rawTimeline);
const workspaceSnapshot = useWorkspaceStore();
const { timelineSummaries } = storeToRefs(workspaceSnapshot);
const summaryList = computed(() => timelineSummaries.value ?? []);
const isEmpty = computed(
  () =>
    // 主链纯 V2：persistedMessages 退出判据，只看摘要 + 活跃 + 乐观。
    summaryList.value.length === 0 && !t.stream.hasActiveTurn && !t.turn.activeTurn?.pendingUser
);

/** 渲染开关一次快照（后续只读）。 */
const renderGate = useP02Flags();

function renderHtml(s: string): string {
  return renderHtmlSmart(s, isP4SubOn('md'));
}

const rowActions = useTimelineRowActions({
  menu: rawTimeline.menu,
  retryTurn: () => {
    void rawTimeline.onRetryTurn();
  },
  findSummary: (turnId: string) => summaryList.value.find((s) => String(s.turnId) === String(turnId)) ?? null,
  getBlockMarkdown: async (blockId: string) => {
    try {
      const { items } = await workspaceSnapshot.getBlockContents([blockId]);
      const block = items[blockId];
      return String(block?.markdown || block?.preview || '');
    } catch {
      return '';
    }
  },
});

provideTimelineScene(t, {
  renderHtml,
  renderStream: renderStreamingText,
  summaryList,
  singleProcess: renderGate.singleProcess,
});

const paneActive = ref(true);
t.setPaneVisible(true);
// 硬切：会话切换直接替换 timelineItems，不挂骨架遮罩 / rAF 采样 / 淡出。
let unbindCopy: (() => void) | null = null;
function onReady(el: HTMLElement | null): void {
  unbindCopy?.();
  unbindCopy = null;
  if (el) unbindCopy = bindCodeCopyDelegation(el);
  t.attachContainer(el);
}
onUnmounted(() => unbindCopy?.());
</script>
