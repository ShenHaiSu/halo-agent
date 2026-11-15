<template>
  <KeepAlive :max="3">
    <div v-show="paneActive" :aria-hidden="paneActive ? undefined : 'true'" class="flex-1 min-h-0 min-w-0 flex flex-col">
      <TimelineView
        :items="t.timelineItems"
        :history-items="t.visibleHistoryEntries"
        :tail-items="t.tailEntries"
        :is-empty="isEmpty"
        :show-jump-btn="!!t.showJump"
        :reading-window="!!t.readingWindow"
        :chat-width-mode="t.chatWidthMode"
        :need02-attr="t.need02Attr"
        @ready="onReady"
        @toggle="rowActions.onToggle($event)"
        @user-menu="(e, item) => rowActions.onUserMenu(e, item)"
        @assistant-menu="(e, item) => rowActions.onAssistantMenu(e, item)"
        @link-click="t.onMarkdownLinkClick($event)"
        @retry="t.onRetryTurn()"
        @retry-pending="rowActions.onRetryPending()"
        @dismiss="t.onDismissError()"
        @menu-copy="t.menu.onMenuCopy()"
        @menu-replay="t.menu.onMenuReplay()"
        @menu-branch="t.menu.onMenuBranch()"
        @menu-delete="t.menu.onMenuDelete()"
        @jump-latest="t.jumpToLatest()"
      />
    </div>
  </KeepAlive>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import TimelineView from './TimelineView.vue';
import { useTimeline } from '../composables/useTimeline';
import { renderHtmlSmart, renderStreamingText } from '@features/session/utils/markdownRender';
import { isP4SubOn, useP02Flags } from '@features/need02/utils/need02Flags';
import { provideTimelineScene } from '../composables/useTimelineScene';
import { useTimelineRowActions } from '../composables/useTimelineRowActions';

/** 装配器：组装视图模型并提供，模板只做数据下发与事件转发。 */
const rawTimeline = useTimeline();
const t = reactive(rawTimeline);
const workspaceSnapshot = useWorkspaceStore();
const { timelineSummaries, isV2Timeline } = storeToRefs(workspaceSnapshot);
const summaryList = computed(() => timelineSummaries.value ?? []);
const isV2WithSummary = computed(() => isV2Timeline?.value === true && summaryList.value.length > 0);
const isEmpty = computed(() => t.persistedMessages.length === 0 && summaryList.value.length === 0 && !t.stream.hasActiveTurn);

/** 渲染开关一次快照（后续只读）。 */
const renderGate = useP02Flags();

function renderHtml(s: string): string {
  return renderHtmlSmart(s, isP4SubOn('md'));
}

const rowActions = useTimelineRowActions({
  menu: rawTimeline.menu,
  toggleTurn: rawTimeline.toggleTurn,
  toggleRun: rawTimeline.toggleRun,
  retryTurn: () => {
    void rawTimeline.onRetryTurn();
  },
});

provideTimelineScene(t, {
  renderHtml,
  renderStream: renderStreamingText,
  summaryList,
  hideLegacyHistory: isV2WithSummary,
  singleProcess: renderGate.singleProcess,
});

const paneActive = ref(true);
t.setPaneVisible(true);
// 硬切：会话切换直接替换 timelineItems，不挂骨架遮罩 / rAF 采样 / 淡出。
function onReady(el: HTMLElement | null): void {
  t.attachContainer(el);
}
</script>
