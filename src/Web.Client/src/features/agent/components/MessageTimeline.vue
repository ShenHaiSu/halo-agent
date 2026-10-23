<template>
  <KeepAlive :max="3">
    <div v-show="paneActive" :aria-hidden="paneActive ? undefined : 'true'" class="flex-1 min-h-0 min-w-0 flex flex-col">
      <TimelineView
        :is-empty="t.persistedMessages.length === 0 && !t.stream.hasActiveTurn"
        :items="t.timelineItems"
        :history-items="t.visibleHistoryEntries"
        :tail-items="t.tailEntries"
        :show-jump-btn="!!t.showJump"
        :reading-window="!!t.readingWindow"
        :veil-covering="veilCovering"
        :chat-width-mode="t.chatWidthMode"
        :is-expanded="t.isTurnExpanded"
        :anchor-ref="t.setAnchor"
        :msg-images="t.msgImages"
        :thumb-of="t.thumbOf"
        :has-lost="t.hasLostImage"
        :render-html="renderMarkdown"
        :render-stream="renderStreamingText"
        :card-for="t.toCard"
        :tool-key="t.toolKeyOf"
        :has-failed="t.hasFailedTool"
        :show-telemetry="t.hasTelemetryGroup"
        :protocol-of="t.protocolOf"
        :cache-line-of="t.cacheLineOf"
        :has-active-turn="t.stream.hasActiveTurn"
        :thinking-line="`思考中… · 本轮 ${t.curIterToolCount} 个工具 · 第 ${t.curIteration} 轮`"
        :is-generating="t.transcriptRunning"
        :segs="t.displaySegs"
        :seg-kill-switch="t.stream.segKillSwitch"
        :stream-md-kill-switch="t.stream.streamMdKillSwitch"
        :fallback-cards="t.cards.toolCards"
        :streaming-text="t.stream.streamingText"
        :show-thinking="t.showThinking && t.transcriptRunning"
        :turn-notice="t.turn.turnNotice"
        :last-error="t.turn.lastError"
        :show-retry="!!t.turn.activeTurn.lastErrorInfo?.retry"
        :live-telemetry="liveTelemetry"
        :menu-visible="t.menu.menuState.visible"
        :menu-x="t.menu.menuState.x"
        :menu-y="t.menu.menuState.y"
        :menu-busy="t.menu.menuBusy"
        :menu-can-copy="t.menu.menuCanCopy"
        @ready="t.attachContainer($event)"
        @toggle="t.toggleTurn($event)"
        @user-menu="(e, item) => onUserMenu(e, item)"
        @assistant-menu="(e, item) => onAssistantMenu(e, item)"
        @link-click="t.onMarkdownLinkClick($event)"
        @retry="t.onRetryTurn()"
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
import TimelineView from './TimelineView.vue';
import { useTimeline } from '../composables/useTimeline';
import { renderMarkdown, renderStreamingText } from '@features/session/utils/markdownRender';
import type { TimelineEntry } from '@features/session/utils/turnGroup';

const t = reactive(useTimeline());
// Hidden pane never follows/pages/samples; 显隐保留滚动（v-show 不重建 DOM）。
const paneActive = ref(true);
t.setPaneVisible(true);
// Settle veil: bounded commit covered, 3 stable frames / 600ms cap in transcriptSettle.
const veilCovering = ref(false);
const liveTelemetry = computed(() =>
  t.showLiveTelemetry
    ? {
        protocol: t.telemetry.telemetry.protocol === 'resp' ? 'RESP (/v1/responses)' : 'CHAT (/v1/chat/completions)',
        ttft: t.telemetry.telemetry.ttftMs,
        input: t.telemetry.telemetry.inputTokens,
        cached: t.telemetry.telemetry.cachedTokens,
        cachePercent: t.liveCachePercent,
        output: t.telemetry.telemetry.outputTokens,
      }
    : null
);
function onUserMenu(e: MouseEvent, item: TimelineEntry): void {
  t.menu.openMenu(e, { kind: 'user', id: String(item.msg?.id || ''), text: String(item.msg?.content || '') });
}
function onAssistantMenu(e: MouseEvent, item: TimelineEntry): void {
  t.menu.openMenu(e, {
    kind: 'assistant',
    id: String(item.group?.assistantId || item.group?.key || ''),
    text: String(item.group?.content || ''),
  });
}
</script>
