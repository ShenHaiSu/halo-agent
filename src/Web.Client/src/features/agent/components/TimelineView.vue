<template>
  <div class="flex-1 min-h-0 min-w-0 max-w-full relative flex flex-col" data-testid="message-timeline" :data-need02="need02Attr">
    <div ref="scroll" class="flex-1 min-h-0 overflow-y-auto min-w-0 max-w-full overflow-x-hidden transcript-scroll">
      <div :class="chatWidthMode === 'full' ? 'chat-timeline-full px-4 py-4 space-y-4' : 'chat-timeline-centered px-4 py-4 space-y-4'">
        <TimelineSummarySection v-if="summaryList && summaryList.length > 0" :summaries="summaryList" />
        <div v-if="isEmpty" class="py-10 text-center space-y-2">
          <div class="flex justify-center">
            <Bot class="w-8 h-8 text-accent" />
          </div>
          <p class="text-sm text-primary font-medium">在下方输入任务指令，Agent 将自动编排工具完成</p>
          <p class="text-xs text-secondary font-mono">默认 Runner：CMD 优先 / PYTHON 次之 · 禁用 PowerShell 专属语法</p>
        </div>
        <TranscriptHistory :entries="historyList">
          <template #default="{ entry }">
            <TimelineRow
              :item="entry as TimelineEntry"
              :is-expanded="isExpanded"
              :is-run-expanded="isRunExpanded"
              :anchor-ref="anchorRef"
              :msg-images="msgImages"
              :thumb-of="thumbOf"
              :has-lost="hasLost"
              :img-lost="imgLost"
              :render-html="renderHtml"
              :card-for="cardFor"
              :tool-key="toolKey"
              :has-failed="hasFailed"
              :failed-count="failedCount"
              :show-telemetry="showTelemetry"
              :protocol-of="protocolOf"
              :cache-line-of="cacheLineOf"
              :reading-window="!!readingWindow"
              :is-pending="isPending"
              :pending-status="pendingStatus"
              :live-segs="[]"
              :is-generating="isGenerating"
              :stream-md-kill-switch="!!streamMdKillSwitch"
              :render-stream="renderStream"
              :is-last="false"
              :live-completing="false"
              :handoff-anim="handoffAnim !== false"
              :single-process="singleProcess !== false"
              @toggle="emit('toggle', $event)"
              @user-menu="(e, item) => emit('user-menu', e, item)"
              @assistant-menu="(e, item) => emit('assistant-menu', e, item)"
              @link-click="emit('link-click', $event)"
              @retry-pending="(item) => emit('retry-pending', item)"
              @retry="emit('retry')"
              @dismiss="emit('dismiss')"
            />
          </template>
        </TranscriptHistory>
        <TranscriptTail :entries="tailList">
          <template #default="{ entry, isLast }">
            <TimelineRow
              :item="entry as TimelineEntry"
              :is-expanded="isExpanded"
              :is-run-expanded="isRunExpanded"
              :anchor-ref="anchorRef"
              :msg-images="msgImages"
              :thumb-of="thumbOf"
              :has-lost="hasLost"
              :img-lost="imgLost"
              :render-html="renderHtml"
              :card-for="cardFor"
              :tool-key="toolKey"
              :has-failed="hasFailed"
              :failed-count="failedCount"
              :show-telemetry="showTelemetry"
              :protocol-of="protocolOf"
              :cache-line-of="cacheLineOf"
              :reading-window="!!readingWindow"
              :is-pending="isPending"
              :pending-status="pendingStatus"
              :live-segs="liveSegsFor(entry as TimelineEntry)"
              :is-generating="isGenerating"
              :stream-md-kill-switch="!!streamMdKillSwitch"
              :render-stream="renderStream"
              :is-last="!!isLast"
              :live-completing="!!liveCompleting"
              :handoff-anim="handoffAnim !== false"
              :single-process="singleProcess !== false"
              :notice-in-turn="!!noticeInTurn"
              :turn-notice="turnNotice"
              :last-error="lastError"
              :show-retry="showRetry"
              @toggle="emit('toggle', $event)"
              @user-menu="(e, item) => emit('user-menu', e, item)"
              @assistant-menu="(e, item) => emit('assistant-menu', e, item)"
              @link-click="emit('link-click', $event)"
              @retry-pending="(item) => emit('retry-pending', item)"
              @retry="emit('retry')"
              @dismiss="emit('dismiss')"
            />
          </template>
        </TranscriptTail>
        <n-alert v-if="turnNotice && !noticeInTurn" type="info" :show-icon="false" class="text-xs">
          {{ turnNotice }}
        </n-alert>
        <n-alert v-if="lastError && !noticeInTurn" type="error" class="text-xs" data-testid="turn-error-alert">
          {{ lastError }}
        </n-alert>
        <div v-if="showRetry && !noticeInTurn" class="flex items-center gap-2">
          <n-button size="small" type="error" ghost data-testid="turn-retry-btn" @click="emit('retry')"> 重试本轮 </n-button>
          <n-button size="small" quaternary data-testid="turn-error-dismiss" @click="emit('dismiss')"> 忽略 </n-button>
        </div>
        <div
          v-if="liveTelemetry"
          class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5 border-t border-subtle pt-2"
          title="IN为末轮快照，多轮工具调用轮内多次请求只记末次占用，累计见顶栏Σ"
        >
          <span>协议: {{ liveTelemetry.protocol }}</span>
          <span v-if="liveTelemetry.ttft !== null">首字: {{ liveTelemetry.ttft }}ms</span>
          <span>In: {{ liveTelemetry.input }} (Cache {{ liveTelemetry.cached }} · {{ liveTelemetry.cachePercent }})</span>
          <span>Out: {{ liveTelemetry.output }}</span>
        </div>
        <MessageContextMenu
          :visible="menuVisible"
          :x="menuX"
          :y="menuY"
          :busy="menuBusy"
          :can-copy="menuCanCopy"
          @copy="emit('menu-copy')"
          @replay="emit('menu-replay')"
          @branch="emit('menu-branch')"
          @delete="emit('menu-delete')"
        />
      </div>
    </div>
    <Transition name="show-jump">
      <button
        v-if="showJumpVisible"
        type="button"
        class="show-jump-pill"
        data-testid="show-jump"
        title="回到底部"
        aria-label="回到底部"
        @click="emit('jump-latest')"
      >
        <ArrowDownIcon :size="18" />
      </button>
    </Transition>
  </div>
</template>
<script setup lang="ts">
import { computed, onMounted, onUnmounted, useTemplateRef, type ComponentPublicInstance } from 'vue';
import { Bot } from 'lucide-vue-next';
import ArrowDownIcon from '@shared/ui/icons/ArrowDownIcon.vue';
import type { ChatImageRef, TurnSummary, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import MessageContextMenu from './MessageContextMenu.vue';
import TimelineRow from './TimelineRow.vue';
import TimelineSummarySection from '@features/session/components/TimelineSummarySection.vue';
import TranscriptHistory from './TranscriptHistory.vue';
import TranscriptTail from './TranscriptTail.vue';

// 尾部分片长度：调用方恒传 historyItems/tailItems 时直接使用，否则按 8 切分。
const TAIL_SIZE = 8;
const props = defineProps<{
  isEmpty: boolean;
  items: TimelineEntry[];
  /** 后端聚合直显：非空时在滚动区顶部渲染拍扁摘要（含过程盒锚点注册）。 */
  summaryList?: TurnSummary[] | null;
  chatWidthMode: string;
  isExpanded: (key: string) => boolean;
  /** need-01：合并舱展开态（缺省回落到 isExpanded，保证老调用方/测试不炸）。 */
  isRunExpanded?: (key: string) => boolean;
  anchorRef: (key: string) => (el: Element | ComponentPublicInstance | null) => void;
  msgImages: (msg: UnifiedChatMessage | undefined) => ChatImageRef[];
  thumbOf: (img: ChatImageRef) => string;
  hasLost: (msg: UnifiedChatMessage | undefined) => boolean;
  /** M1-4：单图丢失判定（可选，缺省时 TimelineRow 回落到 img.lost）。 */
  imgLost?: (img: ChatImageRef) => boolean;
  renderHtml: (text: string) => string;
  renderStream: (text: string) => string;
  cardFor: (m: UnifiedChatMessage, g: TurnGroup) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
  hasFailed: (g: TurnGroup) => boolean;
  failedCount?: (g: TurnGroup) => number;
  showTelemetry: (g: TurnGroup) => boolean;
  protocolOf: (g: TurnGroup) => string;
  cacheLineOf: (g: TurnGroup) => string;
  thinkingLine: string;
  isGenerating: boolean;
  /** need23-R5：流式 Markdown 逃生开关（true=回落纯文本）。 */
  streamMdKillSwitch?: boolean;
  /** need02-P2: inner live body for UserTurnBlock (empty = steady render). */
  liveSegs?: Array<{ kind: string; key: string; text: string; card: unknown }>;
  turnNotice: string;
  lastError: string;
  showRetry: boolean;
  liveTelemetry: { protocol: string; ttft: number | null; input: number; cached: number; cachePercent: string; output: number } | null;
  menuVisible: boolean;
  menuX: number;
  menuY: number;
  menuBusy: boolean;
  menuCanCopy: boolean;
  historyItems?: TimelineEntry[] | null;
  tailItems?: TimelineEntry[] | null;
  showJumpBtn?: boolean;
  readingWindow?: boolean;
  /** need02-P0：开关透传串（data-need02），缺省 undefined 时不渲染该属性，零视觉变更。 */
  need02Attr?: string;
  /** need02-P1：pending 判定/状态（可选，缺省时 TimelineRow 回落到 id 前缀）。 */
  isPending?: (msg: UnifiedChatMessage | undefined) => boolean;
  pendingStatus?: (msg: UnifiedChatMessage | undefined) => string;
  /** need02-P3：轮末交接窗（model.liveCompleting；仅末轮行透给 UserTurnBlock，见 is-last 门控）。 */
  liveCompleting?: boolean;
  /** need02-P3：交接动画开关（p3；false = P2 硬切）。缺省开（老调用方行为不变）。 */
  handoffAnim?: boolean;
  /** 轮内 notice（true 时全局 alert 隐藏，notice 收进末轮底部）。 */
  noticeInTurn?: boolean;
  /** 单容器三段模板开关；默认开。 */
  singleProcess?: boolean;
  /** S3 增补 F-sup-2：v2 且有摘要时旧链已提交部分不挂载（仅保留 live 段）。 */
  hideLegacyHistory?: boolean;
  /** S3 增补 F-sup-2：当前是否有活跃轮（流式态保留容器防真空帧）。 */
  hasActiveTurn?: boolean;
}>();
const emit = defineEmits<{
  ready: [el: HTMLElement | null];
  toggle: [key: string];
  'user-menu': [e: MouseEvent, item: TimelineEntry];
  'assistant-menu': [e: MouseEvent, item: TimelineEntry];
  'link-click': [e: MouseEvent];
  retry: [];
  dismiss: [];
  'menu-copy': [];
  'menu-replay': [];
  'menu-branch': [];
  'menu-delete': [];
  'jump-latest': [];
  'retry-pending': [item: TimelineEntry];
}>();
const scroll = useTemplateRef<HTMLElement>('scroll');
onMounted(() => emit('ready', scroll.value));
onUnmounted(() => emit('ready', null));
const historyList = computed<TimelineEntry[]>(() => {
  // S3 增补 F-sup-2：v2 且有摘要时旧链已提交部分不挂载。
  if (props.hideLegacyHistory) return [];
  if (props.historyItems) return props.historyItems;
  return props.items.slice(0, Math.max(0, props.items.length - TAIL_SIZE));
});
const tailList = computed<TimelineEntry[]>(() => {
  const raw: TimelineEntry[] = (() => {
    if (props.tailItems) return props.tailItems;
    if (props.historyItems) return [];
    return props.items.slice(Math.max(0, props.items.length - TAIL_SIZE));
  })();
  // S3 增补 F-sup-2：v2 下尾部仅保留 live 段（活跃轮或 pending 归属轮），保证发送瞬间与流式态仍有容器。
  if (!props.hideLegacyHistory) return raw;
  if (props.hasActiveTurn) return raw;
  try {
    const keep = raw.filter((e) => {
      try {
        if (props.isPending && (e as TimelineEntry).msg && props.isPending((e as TimelineEntry).msg)) return true;
      } catch {
        /* ignore */
      }
      const k = String((e as unknown as { key?: string }).key || '');
      return !!k && k === liveOwnerKey.value && (props.liveSegs || []).length > 0;
    });
    return keep;
  } catch {
    return [];
  }
});
/**
 * live 归属键：最后一个 user-turn 条目（全量 items，不看 tail 切片）。
 * 旧做法用 TranscriptTail 的 isLast（= 尾部数组末项）判定，末项往往是 user 或 system；
 * 新 user 刚发出、尚无 assistant 条目时末项是旧轮，live 段被挂到旧轮 → 折叠栏跑到用户消息上方。
 * 这里以「当前会话最后一个用户轮」为唯一归属，只有它能拿到 liveSegs。
 */
const liveOwnerKey = computed<string>(() => {
  const all = props.items || [];
  for (let i = all.length - 1; i >= 0; i--) {
    const e = all[i] as TimelineEntry & { turn?: { key?: string } };
    if (e.kind === 'user-turn') return String(e.turn?.key || e.key || '');
  }
  return '';
});
type LiveSeg = { kind: string; key: string; text: string; card: unknown };
function liveSegsFor(entry: TimelineEntry): LiveSeg[] {
  const e = entry as TimelineEntry & { turn?: { key?: string } };
  if (e.kind !== 'user-turn') return [];
  const key = String(e.turn?.key || e.key || '');
  if (!key || key !== liveOwnerKey.value) return [];
  return (props.liveSegs || []) as LiveSeg[];
}
// 硬切：回到底部按钮直接显隐，无遮罩互斥。
const showJumpVisible = computed(() => props.showJumpBtn || props.readingWindow);
</script>
<style scoped>
/* M1-6 滚动锚定与滚动条槽位：配合 M2 的渐进跟随才真正生效——
   单独声明时会被 scrollTop 硬赋值破坏锚定链，故此项只铺声明、不在 M1 验收。 */
.transcript-scroll {
  overflow-anchor: auto;
  scrollbar-gutter: stable;
}
/* ---- M3 过渡动画 ----
   核心约束（docs/99 §2、§4）：高度不做任何动画；leave 必须 absolute 脱流，
   否则段毕业时会「下移（leaving 仍占位）→ 上移（移除）」两次抖动，比不加动画更糟。 */
.live-turn-enter-active {
  transition:
    opacity 0.16s ease,
    transform 0.16s ease;
}
.live-turn-enter-from {
  opacity: 0;
  transform: translateY(6px);
}
.live-turn-leave-active {
  transition: opacity 0.14s ease;
}
.live-turn-leave-to {
  opacity: 0;
}
.seg-list {
  /* TransitionGroup 的 leave 元素 absolute 化后需要一个定位容器 */
  position: relative;
}
.seg-enter-active {
  transition:
    opacity 0.14s ease,
    transform 0.14s ease;
}
.seg-enter-from {
  opacity: 0;
  transform: translateY(-4px);
}
.seg-leave-active {
  position: absolute;
  left: 0;
  right: 0;
  transition: opacity 0.12s ease;
  pointer-events: none;
}
.seg-leave-to {
  opacity: 0;
}
@media (prefers-reduced-motion: reduce) {
  .live-turn-enter-active,
  .live-turn-leave-active,
  .seg-enter-active,
  .seg-leave-active {
    transition: none;
  }
}
/* 回到底部圆钮：absolute 悬浮于滚动容器之外，不占 scrollHeight，避免出现又收回的抖动。 */
.show-jump-pill {
  position: absolute;
  bottom: 16px;
  right: 16px;
  width: 36px;
  height: 36px;
  padding: 0;
  border-radius: 50%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: var(--accent-primary, #3b82f6);
  color: #fff;
  opacity: 0.75;
  border: none;
  cursor: pointer;
  z-index: 6;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.25);
  transition:
    opacity 150ms ease-out,
    background-color 150ms ease-out;
}
.show-jump-pill:hover {
  opacity: 1;
}
.show-jump-pill:active {
  opacity: 1;
}
.show-jump-pill:focus-visible {
  opacity: 1;
  outline: 2px solid var(--accent-primary, #3b82f6);
  outline-offset: 2px;
}
html.light .show-jump-pill {
  color: #0f172a;
}
/* P03：圆钮淡入（与会话切换无关，保留） */
.show-jump-enter-active {
  transition: opacity 150ms ease-out;
}
.show-jump-enter-from {
  opacity: 0;
}
@media (prefers-reduced-motion: reduce) {
  .show-jump-pill,
  .show-jump-enter-active {
    transition: none;
  }
}
</style>
