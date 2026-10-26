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
        :has-active-turn="t.stream.hasActiveTurn && !t.need02Flags.p2"
        :thinking-line="`思考中… · 本轮 ${t.curIterToolCount} 个工具 · 第 ${t.curIteration} 轮`"
        :is-generating="t.transcriptRunning"
        :segs="t.displaySegs"
        :live-segs="t.liveInclusiveSegs"
        :seg-kill-switch="t.stream.segKillSwitch"
        :stream-md-kill-switch="t.stream.streamMdKillSwitch"
        :need02-attr="t.need02Attr"
        :is-pending="t.isPendingMessage"
        :pending-status="t.pendingStatusOf"
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
import { computed, onUnmounted, reactive, ref, watch } from 'vue';
import TimelineView from './TimelineView.vue';
import { useTimeline } from '../composables/useTimeline';
import { renderMarkdown, renderMarkdownCached, renderStreamingText } from '@features/session/utils/markdownRender';
import { isP4SubOn } from '@features/need02/utils/need02Flags';
import { turnCopyText, turnMessageIds, turnReplayAnchor } from '@features/session/utils/userTurn';
import type { UserTurn } from '@features/session/utils/userTurn';
import { createSettleController, type SettleController } from '@features/session/utils/transcriptSettle';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import { useWorkspaceStore } from '@/stores/workspaceStore';

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
// Hidden pane never follows/pages/samples; 显隐保留滚动（v-show 不重建 DOM）。
const paneActive = ref(true);
t.setPaneVisible(true);

/**
 * M4-§2：settle veil（骨架遮罩）**只用于会话切换 / 历史大量加载**。
 *
 * 红线（docs/99 §7）：**绝不用在轮末**——`turnEvents` 已实现「先 reloadNow 后清容器」，
 * 轮末内容不会真空，此时叠遮罩等于人为制造一次白闪。
 *
 * 施工陷阱（docs/05 §2.4）：`createSettleController` 的 `sample()` 只能由外部喂，
 * 内部没有采样循环；不喂 sample 就只能靠 600ms 超时兜底 → **固定遮挡 600ms**，比白闪更糟。
 * 所以这里必须挂一个 rAF 采样循环，并在遮罩结束后立刻停掉。
 */
const veilCovering = ref(false);
const VEIL_MIN_MESSAGES = 5;
const workspaceStore = useWorkspaceStore();
let settleCtrl: SettleController | null = null;
let sampleEl: HTMLElement | null = null;
let sampleRaf = 0;

function stopSampling(): void {
  if (sampleRaf) cancelAnimationFrame(sampleRaf);
  sampleRaf = 0;
}

function startSampling(): void {
  stopSampling();
  const loop = (): void => {
    sampleRaf = 0;
    if (!veilCovering.value || !settleCtrl || !sampleEl) return;
    settleCtrl.sample({ scrollHeight: sampleEl.scrollHeight, clientHeight: sampleEl.clientHeight });
    if (!veilCovering.value) return;
    sampleRaf = requestAnimationFrame(loop);
  };
  sampleRaf = requestAnimationFrame(loop);
}

function beginSettle(): void {
  settleCtrl?.dispose();
  stopSampling();
  // 短会话（新会话/极少消息）不遮：本来就不会露白，闪一下骨架屏反而更糟。
  const shortSession = (t.persistedMessages?.length || 0) < VEIL_MIN_MESSAGES;
  settleCtrl = createSettleController({ shortSession });
  veilCovering.value = settleCtrl.veilCovering;
  settleCtrl.onPhase = () => {
    veilCovering.value = settleCtrl ? settleCtrl.veilCovering : false;
    if (!veilCovering.value) stopSampling();
  };
  if (veilCovering.value) startSampling();
}

watch(
  () => workspaceStore.activeSessionId,
  () => beginSettle()
);
onUnmounted(() => {
  stopSampling();
  settleCtrl?.dispose();
  settleCtrl = null;
});

function onReady(el: HTMLElement | null): void {
  sampleEl = el;
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
  t.menu.openMenu(e, {
    kind: 'assistant',
    id: String(item.group?.assistantId || item.group?.key || ''),
    text: String(item.group?.content || ''),
  });
}
/** need02-P1：pending 失败重发（新 requestId，带图，旧 failed 不复用）。 */
function onRetryPending(_item: TimelineEntry): void {
  void _item;
  void t.onRetryTurn();
}
</script>
