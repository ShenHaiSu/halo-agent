<template>
  <div
    ref="scroll"
    class="flex-1 min-h-0 overflow-y-auto min-w-0 max-w-full overflow-x-hidden relative"
    data-testid="message-timeline"
    :data-transcript-settling="veilCovering ? 'true' : undefined"
  >
    <div :class="chatWidthMode === 'full' ? 'chat-timeline-full px-4 py-4 space-y-4' : 'chat-timeline-centered px-4 py-4 space-y-4'">
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
            :anchor-ref="anchorRef"
            :msg-images="msgImages"
            :thumb-of="thumbOf"
            :has-lost="hasLost"
            :render-html="renderHtml"
            :card-for="cardFor"
            :tool-key="toolKey"
            :has-failed="hasFailed"
            :show-telemetry="showTelemetry"
            :protocol-of="protocolOf"
            :cache-line-of="cacheLineOf"
            @toggle="emit('toggle', $event)"
            @user-menu="(e, item) => emit('user-menu', e, item)"
            @assistant-menu="(e, item) => emit('assistant-menu', e, item)"
            @link-click="emit('link-click', $event)"
          />
        </template>
      </TranscriptHistory>
      <TranscriptTail :entries="tailList">
        <template #default="{ entry }">
          <TimelineRow
            :item="entry as TimelineEntry"
            :is-expanded="isExpanded"
            :anchor-ref="anchorRef"
            :msg-images="msgImages"
            :thumb-of="thumbOf"
            :has-lost="hasLost"
            :render-html="renderHtml"
            :card-for="cardFor"
            :tool-key="toolKey"
            :has-failed="hasFailed"
            :show-telemetry="showTelemetry"
            :protocol-of="protocolOf"
            :cache-line-of="cacheLineOf"
            @toggle="emit('toggle', $event)"
            @user-menu="(e, item) => emit('user-menu', e, item)"
            @assistant-menu="(e, item) => emit('assistant-menu', e, item)"
            @link-click="emit('link-click', $event)"
          />
        </template>
        <template #live>
          <div
            v-if="hasActiveTurn"
            data-testid="active-turn-container"
            class="rounded-lg border border-subtle bg-surface/40 px-3 py-2 space-y-2 min-w-0"
          >
            <div class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] select-none">
              <BrainCircuit class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
              <span>{{ thinkingLine }}</span>
              <Loader2 v-if="isGenerating" class="w-3 h-3 text-indigo-400 animate-spin shrink-0" />
            </div>
            <template v-if="!segKillSwitch && segs.length">
              <template v-for="seg in segs" :key="seg.key">
                <div
                  v-if="seg.kind === 'reasoning' && seg.text"
                  class="whitespace-pre-wrap break-words app-text-small text-[var(--reasoning-text)] max-h-24 overflow-y-auto"
                  :data-seg="seg.key"
                >
                  {{ seg.text }}
                </div>
                <div v-else-if="seg.kind === 'tool' && seg.card" data-testid="active-turn-tools" :data-seg="seg.key">
                  <ToolCallCard :card="seg.card" :default-collapsed="true" />
                </div>
                <div
                  v-else-if="seg.kind === 'text' && seg.text"
                  class="app-text-body leading-relaxed text-primary break-words select-text min-w-0"
                  :data-seg="seg.key"
                  @click="emit('link-click', $event)"
                >
                  <MarkdownStreamBlocks v-if="!streamMdKillSwitch" :text="seg.text" :cache-key="seg.key" />
                  <div v-else style="white-space: pre-wrap" v-html="renderStream(seg.text)" />
                </div>
              </template>
            </template>
            <template v-else-if="segKillSwitch">
              <div v-if="fallbackCards.length" class="space-y-1.5" data-testid="active-turn-tools">
                <ToolCallCard v-for="card in fallbackCards" :key="card.callId" :card="card" :default-collapsed="true" />
              </div>
              <div
                v-if="streamingText"
                class="app-text-body leading-relaxed text-primary break-words select-text min-w-0"
                style="white-space: pre-wrap"
                @click="emit('link-click', $event)"
                v-html="renderStream(streamingText)"
              />
            </template>
            <div v-if="showThinking" class="flex items-center gap-2 text-xs text-secondary">
              <Loader2 class="w-3.5 h-3.5 animate-spin" />
              <span>Agent 推理中...</span>
            </div>
          </div>
        </template>
      </TranscriptTail>
      <n-alert v-if="turnNotice" type="info" :show-icon="false" class="text-xs">
        {{ turnNotice }}
      </n-alert>
      <n-alert v-if="lastError" type="error" class="text-xs" data-testid="turn-error-alert">
        {{ lastError }}
      </n-alert>
      <div v-if="showRetry" class="flex items-center gap-2">
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
    <div v-if="veilCovering" class="transcript-veil" aria-hidden="true">
      <div class="transcript-skeleton" />
      <div class="transcript-skeleton short" />
      <div class="transcript-skeleton" />
    </div>
    <button
      v-if="showJumpVisible"
      type="button"
      class="show-jump-pill"
      data-testid="show-jump"
      title="回到底部"
      @click="emit('jump-latest')"
    >
      回到底部
    </button>
  </div>
</template>
<script setup lang="ts">
import { computed, onMounted, onUnmounted, useTemplateRef, type ComponentPublicInstance } from 'vue';
import { Bot, BrainCircuit, Loader2 } from 'lucide-vue-next';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import type { DisplaySeg } from '../composables/useTimeline';
import ToolCallCard from './ToolCallCard.vue';
import MarkdownStreamBlocks from './MarkdownStreamBlocks.vue';
import MessageContextMenu from './MessageContextMenu.vue';
import TimelineRow from './TimelineRow.vue';
import TranscriptHistory from './TranscriptHistory.vue';
import TranscriptTail from './TranscriptTail.vue';

const TAIL_FALLBACK = 8;

const props = defineProps<{
  isEmpty: boolean;
  items: TimelineEntry[];
  chatWidthMode: string;
  isExpanded: (key: string) => boolean;
  anchorRef: (key: string) => (el: Element | ComponentPublicInstance | null) => void;
  msgImages: (msg: UnifiedChatMessage | undefined) => ChatImageRef[];
  thumbOf: (img: ChatImageRef) => string;
  hasLost: (msg: UnifiedChatMessage | undefined) => boolean;
  renderHtml: (text: string) => string;
  renderStream: (text: string) => string;
  cardFor: (m: UnifiedChatMessage, g: TurnGroup) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
  hasFailed: (g: TurnGroup) => boolean;
  showTelemetry: (g: TurnGroup) => boolean;
  protocolOf: (g: TurnGroup) => string;
  cacheLineOf: (g: TurnGroup) => string;
  hasActiveTurn: boolean;
  thinkingLine: string;
  isGenerating: boolean;
  segs: DisplaySeg[];
  segKillSwitch: boolean;
  /** need23-R5：流式 Markdown 逃生开关（true=回落纯文本）。 */
  streamMdKillSwitch?: boolean;
  fallbackCards: AgentToolCard[];
  streamingText: string;
  showThinking: boolean;
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
  veilCovering?: boolean;
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
}>();
const scroll = useTemplateRef<HTMLElement>('scroll');
onMounted(() => emit('ready', scroll.value));
onUnmounted(() => emit('ready', null));

const historyList = computed<TimelineEntry[]>(() => {
  if (props.historyItems) return props.historyItems;
  return props.items.slice(0, Math.max(0, props.items.length - TAIL_FALLBACK));
});
const tailList = computed<TimelineEntry[]>(() => {
  if (props.tailItems) return props.tailItems;
  if (props.historyItems) return [];
  return props.items.slice(Math.max(0, props.items.length - TAIL_FALLBACK));
});
// 显示条件 (showJump||readingWindow)&&!veilCovering
const showJumpVisible = computed(() => (props.showJumpBtn || props.readingWindow) && !props.veilCovering);
</script>
<style scoped>
.transcript-veil {
  position: absolute;
  inset: 0;
  background: var(--n-body-color, #fff);
  pointer-events: auto;
  z-index: 5;
}
.transcript-skeleton {
  height: 56px;
  border-radius: 8px;
  margin: 12px 16px;
  background: linear-gradient(90deg, #8881 25%, #8883 50%, #8881 75%);
  background-size: 200% 100%;
  animation: veil-shimmer 1.2s linear infinite;
}
.transcript-skeleton.short {
  width: 60%;
}
@keyframes veil-shimmer {
  to {
    background-position: -200% 0;
  }
}
.show-jump-pill {
  position: sticky;
  bottom: 12px;
  float: right;
  margin-right: 16px;
  height: 32px;
  padding: 0 12px;
  border-radius: 9999px;
  font-size: 11px;
  white-space: nowrap;
  background: var(--n-primary-color, #2080f0);
  color: #fff;
  border: none;
  cursor: pointer;
  z-index: 6;
}
</style>
