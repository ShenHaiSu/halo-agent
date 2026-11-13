<template>
  <KeepAlive :max="3">
    <div v-show="paneActive" :aria-hidden="paneActive ? undefined : 'true'" class="flex-1 min-h-0 min-w-0 flex flex-col">
      <TimelineView
        :is-empty="t.persistedMessages.length === 0 && summaryList.length === 0 && !t.stream.hasActiveTurn"
        :summary-list="summaryList"
        :hide-legacy-history="isV2WithSummary"
        :has-active-turn="!!t.stream.hasActiveTurn"
        :items="t.timelineItems"
        :history-items="t.visibleHistoryEntries"
        :tail-items="t.tailEntries"
        :show-jump-btn="!!t.showJump"
        :reading-window="!!t.readingWindow"
        :chat-width-mode="t.chatWidthMode"
        :is-expanded="t.isTurnExpanded"
        :is-run-expanded="t.isRunExpanded"
        :anchor-ref="t.setAnchor"
        :msg-images="t.msgImages"
        :thumb-of="t.thumbOf"
        :has-lost="t.hasLostImage"
        :img-lost="t.isImageLost"
        :render-html="renderHtml"
        :render-stream="renderStreamingText"
        :card-for="t.toCard"
        :tool-key="t.toolKeyOf"
        :has-failed="t.hasFailedTool"
        :failed-count="t.failedToolCount"
        :show-telemetry="t.hasTelemetryGroup"
        :protocol-of="t.protocolOf"
        :cache-line-of="t.cacheLineOf"
        :live-completing="!!t.liveCompleting"
        :handoff-anim="!!t.handoffAnim"
        :notice-in-turn="!!t.noticeInTurn"
        :thinking-line="`思考中… · 本轮 ${t.curIterToolCount} 个工具 · 第 ${t.curIteration} 轮`"
        :single-process="p02.singleProcess"
        :is-generating="t.transcriptRunning"
        :live-segs="t.liveInclusiveSegs"
        :stream-md-kill-switch="t.stream.streamMdKillSwitch"
        :need02-attr="t.need02Attr"
        :is-pending="t.isPendingMessage"
        :pending-status="t.pendingStatusOf"
        :turn-notice="t.turn.turnNotice"
        :last-error="t.turn.lastError"
        :show-retry="!!t.turn.activeTurn.lastErrorInfo?.retry"
        :live-telemetry="liveTelemetry"
        :menu-visible="t.menu.menuState.visible"
        :menu-x="t.menu.menuState.x"
        :menu-y="t.menu.menuState.y"
        :menu-busy="t.menu.menuBusy"
        :menu-can-copy="t.menu.menuCanCopy"
        @ready="onReady"
        @toggle="onToggle($event)"
        @user-menu="(e, item) => onUserMenu(e, item)"
        @assistant-menu="(e, item) => onAssistantMenu(e, item)"
        @link-click="t.onMarkdownLinkClick($event)"
        @retry="t.onRetryTurn()"
        @retry-pending="onRetryPending($event)"
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
import { renderMarkdown, renderMarkdownCached, renderStreamingText } from '@features/session/utils/markdownRender';
import { isP4SubOn, useP02Flags } from '@features/need02/utils/need02Flags';
import { turnCopyText, turnMessageIds, turnReplayAnchor } from '@features/session/utils/userTurn';
import type { UserTurn } from '@features/session/utils/userTurn';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
const workspaceSnapshot = useWorkspaceStore();
const { timelineSummaries, isV2Timeline } = storeToRefs(workspaceSnapshot);
const summaryList = computed(() => timelineSummaries.value ?? []);
/** S3 增补 F-sup-2 门控：v2 且有摘要时旧链已提交部分不挂载（无会话按旧链渲染防白屏）。 */
const isV2WithSummary = computed(() => isV2Timeline?.value === true && summaryList.value.length > 0);

/** need02-P4（p4-md）：稳态渲染走 LRU 缓存；子开关 off 时回直跑（开关即时生效，无需重载，供 G6 矩阵验证）。 */
function renderHtml(s: string): string {
  try {
    if (isP4SubOn('md')) return renderMarkdownCached(s);
  } catch {
    /* fallthrough 直跑 */
  }
  return renderMarkdown(s);
}

const t = reactive(useTimeline());
/** 渲染开关一次快照（后续只读）。 */
const p02 = useP02Flags();
if (import.meta.env.DEV) {
  try {
    console.debug('[p02] flags snapshot', { ...p02 });
  } catch {
    /* ignore */
  }
}
const paneActive = ref(true);
t.setPaneVisible(true);
// 硬切：会话切换直接替换 timelineItems，不挂骨架遮罩 / rAF 采样 / 淡出。
function onReady(el: HTMLElement | null): void {
  t.attachContainer(el);
}
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
/**
 * need-01：合并舱与普通轮共用同一条 toggle 事件；按 key 前缀分流，
 * 避免在 TimelineView 里再加一个 emit 通道（两处 slot 模板都要跟着改）。
 */
function onToggle(key: string): void {
  // need02-P2：uturn- 与 run- 共用同一套显式展开态（runExplicit）。
  if (key.startsWith('run-') || key.startsWith('uturn-')) t.toggleRun(key);
  else t.toggleTurn(key);
}
function turnOf(item: TimelineEntry): UserTurn | null {
  try {
    const u = item as unknown as { kind?: string; turn?: UserTurn };
    if (u && u.kind === 'user-turn' && u.turn) return u.turn;
  } catch {
    /* ignore */
  }
  return null;
}
function onUserMenu(e: MouseEvent, item: TimelineEntry): void {
  // need02-P4（P4b#5）：轮级菜单——复制整轮（user+全文展开）、重放整轮、分支、删除整轮（二次确认一次）。
  const turn = turnOf(item);
  if (turn) {
    const anchor = turnReplayAnchor(turn);
    const ids = turnMessageIds(turn);
    t.menu.openTurnMenu(e, {
      kind: 'user',
      id: anchor ? anchor.id : String(turn.userId || ''),
      text: turnCopyText(turn),
      turnIds: ids,
      turnLabel: `整轮（${ids.length}条）`,
    });
    return;
  }
  t.menu.openMenu(e, { kind: 'user', id: String(item.msg?.id || ''), text: String(item.msg?.content || '') });
}
function onAssistantMenu(e: MouseEvent, item: TimelineEntry): void {
  // need02-P4（P4b#5）：assistant 区右键同样按轮（锚点取本轮末 assistant，重放/分支/删除整轮）。
  const turn = turnOf(item);
  if (turn) {
    let assistantId = '';
    for (let i = turn.steps.length - 1; i >= 0; i--) {
      const sid = String(turn.steps[i]?.assistantId || '');
      if (sid) {
        assistantId = sid;
        break;
      }
    }
    const anchor = turnReplayAnchor(turn);
    const ids = turnMessageIds(turn);
    t.menu.openTurnMenu(e, {
      kind: 'assistant',
      id: assistantId || (anchor ? anchor.id : String(turn.userId || '')),
      text: turnCopyText(turn),
      turnIds: ids,
      turnLabel: `整轮（${ids.length}条）`,
    });
    return;
  }
  if ((item as { kind?: string }).kind === 'turn') {
    // 子会话目录兼容：旧轮次条目仍按轮组信息开菜单。
    t.menu.openMenu(e, {
      kind: 'assistant',
      id: String(item.group?.assistantId || item.group?.key || ''),
      text: String(item.group?.content || ''),
    });
    return;
  }
  t.menu.openMenu(e, {
    kind: 'assistant',
    id: String(item.msg?.id || ''),
    text: String(item.msg?.content || ''),
  });
}
/** need02-P1：pending 失败重发（新 requestId，带图，旧 failed 不复用）。 */
function onRetryPending(_item: TimelineEntry): void {
  void _item;
  void t.onRetryTurn();
}
</script>
