<!-- need02-P2：用户轮次渲染体（D1=B：user 气泡 + 上折叠行 + 下末段直出；流态全量展开）。
     高度零动画（05 R6）：展开/收起只用 n-collapse-transition；本文件不声明任何 height/max-height transition。 -->
<template>
  <div class="turn-body min-w-0 space-y-2" data-testid="user-turn-block" :data-turn-key="turn.key">
    <!-- user 头（含乐观 pending 态；图片槽位复用 96px 恒定槽，零高度跳变） -->
    <div v-if="turn.userMsg" class="flex justify-end">
      <div
        class="max-w-[85%] rounded-lg bg-elevated border px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
        :class="pendingStatus === 'failed' ? 'border-red-400' : 'border-subtle'"
        @contextmenu.prevent="emit('user-menu', $event)"
      >
        <template v-for="(part, pi) in userParts(String(turn.userMsg.content ?? ''))" :key="pi">
          <span v-if="part.type === 'text'">{{ part.text }}</span>
          <SkillPillChip v-else :scope="part.scope" :name="part.name" class="inline-chip" />
        </template>
        <div v-if="userImgs.length" class="mt-1.5 flex flex-wrap gap-1.5" data-testid="msg-images">
          <template v-for="img in userImgs" :key="img.imageId">
            <n-image
              v-if="thumbOfWrap(img)"
              :src="thumbOfWrap(img)"
              :alt="img.fileName"
              width="96"
              height="96"
              object-fit="cover"
              class="rounded border msg-thumb-slot"
            />
            <n-tag v-else-if="imgLostWrap(img)" size="small" :bordered="true" :title="img.fileName" class="msg-thumb-slot">
              {{ img.fileName }}
            </n-tag>
            <div v-else class="msg-thumb-slot msg-thumb-skeleton" :title="img.fileName" aria-busy="true" />
          </template>
        </div>
        <div v-if="pendingStatus === 'sending'" data-testid="pending-sending" class="mt-1 flex items-center justify-end gap-1 text-[11px] text-secondary select-none">
          <span class="pending-spinner" aria-hidden="true" />
          <span>发送中…</span>
        </div>
        <div v-else-if="pendingStatus === 'failed'" data-testid="pending-failed" class="mt-1 flex items-center justify-end gap-1.5 text-[11px] text-red-500 select-none">
          <span>发送失败·点击重发</span>
          <button type="button" data-testid="pending-retry-btn" class="pending-retry-btn" @click.stop="emit('retry-pending')">重发</button>
        </div>
      </div>
    </div>

    <!-- 流态/交接：运行中全量展开；轮末 300ms 交接窗内与终态共存交叉淡入淡出（P3 编排，UserTurnTransition）。
         readingWindow 期 running 置 false（暂停交接动画，直接切终态，05 R11）。 -->
    <UserTurnTransition :running="turn.isRunning && !readingWindow" :completing="completingEffective" :anim-on="handoffAnimOn">
      <template #live>
        <div class="rounded-lg border border-subtle bg-surface/40 px-3 py-2 space-y-2 min-w-0" data-testid="user-turn-live">
          <div v-for="(seg, i) in liveSegs" :key="seg.key" :data-testid="`user-turn-live-seg-${i}`">
            <StreamSegItem :seg="seg" :stream-md-kill-switch="!!streamMdKillSwitch" :render-stream="renderStreamFn" :is-tail="i === liveSegs.length - 1" :is-generating="!!isGenerating" @link-click="emit('link-click', $event)" />
          </div>
          <div v-if="streamingText" class="app-text-body leading-relaxed text-primary break-words select-text min-w-0" style="white-space: pre-wrap" @click="emit('link-click', $event)" v-html="renderStreamFn(streamingText)" />
          <div v-if="!liveSegs.length && !streamingText" class="flex items-center gap-2 text-xs text-secondary">
            <Loader2 class="w-3.5 h-3.5 animate-spin" />
            <span>Agent 推理中...</span>
          </div>
        </div>
      </template>
      <template #default>
        <!-- 稳态单步直出：user + 正文 markdown（与今日一致，不套汇总行，05 R5）。
             运行中（非交接窗）不渲染稳态，避免与 LiveBody 双内容；交接窗内共存做交叉淡入。 -->
        <div v-if="showSteady && isFlat" class="min-w-0">
          <div
            v-if="lastContent"
            class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
            data-testid="user-turn-single"
            @click="emit('link-click', $event)"
            @contextmenu.prevent="emit('assistant-menu', $event)"
            v-html="renderHtmlFn(lastContent)"
          />
        </div>

        <!-- 稳态多步：上折叠行 + 下末段直出 -->
        <div v-else-if="showSteady" class="min-w-0 space-y-2">
          <UserTurnSummary :turn="turn" :expanded="expanded" :reading-window="!!readingWindow" @toggle="emit('toggle', $event)" />
          <n-collapse-transition :show="expanded">
            <div class="px-3 pb-2 space-y-2 max-h-72 overflow-y-auto min-w-0 rounded-r-md bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70">
              <div v-for="(st, i) in earlier" :key="st.assistantId || `step-${i}`" class="min-w-0" :data-testid="`user-turn-step-${i + 1}`">
                <div class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] select-none">
                  <span class="font-mono shrink-0 opacity-70">{{ st.iteration }}</span>
                  <span class="truncate">第 {{ st.iteration }} 步</span>
                  <span v-if="st.reasoning" class="shrink-0 opacity-60">· 思考</span>
                  <span v-if="(st.toolMessages?.length || 0) > 0" class="shrink-0 font-mono opacity-60">· {{ st.toolMessages.length }} 个工具</span>
                </div>
                <div v-if="st.content" class="mt-1 app-text-body leading-relaxed text-primary break-words select-text min-w-0" @click="emit('link-click', $event)" v-html="renderHtmlFn(st.content)" />
                <div v-if="st.reasoning" class="mt-1 whitespace-pre-wrap break-words select-text app-text-small text-[var(--reasoning-text)] max-h-[40vh] overflow-y-auto">
                  {{ st.reasoning }}
                </div>
                <div v-if="(st.toolMessages?.length || 0) > 0" class="mt-1 space-y-1.5 min-w-0">
                  <ToolCallCard v-for="t in st.toolMessages" :key="toolKeyFn(t)" :card="cardForFn(t, groupOf(st))" :default-collapsed="true" />
                </div>
              </div>
            </div>
          </n-collapse-transition>
          <div
            v-if="lastContent"
            class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
            @click="emit('link-click', $event)"
            @contextmenu.prevent="emit('assistant-menu', $event)"
            v-html="renderHtmlFn(lastContent)"
          />
        </div>
      </template>
    </UserTurnTransition>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import { Loader2 } from 'lucide-vue-next';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { AgentToolCard } from '@features/agent/stores/model';
import type { DisplaySeg } from '@features/agent/composables/useTimeline';
import type { TurnGroup } from '@features/session/utils/turnGroup';
import type { UserTurn, UserTurnStep } from '@features/session/utils/userTurn';
import { earlierSteps, isSingleFlatTurn, lastStepContent } from '@features/session/utils/userTurn';
import { userParts } from '@features/agent/utils/userParts';
import SkillPillChip from './SkillPillChip.vue';
import ToolCallCard from './ToolCallCard.vue';
import StreamSegItem from './StreamSegItem.vue';
import UserTurnSummary from './UserTurnSummary.vue';
import UserTurnTransition from './UserTurnTransition.vue';

const props = defineProps<{
  turn: UserTurn;
  expanded: boolean;
  readingWindow?: boolean;
  renderHtml: (text: string) => string;
  renderStream?: (text: string) => string;
  cardFor: (m: UnifiedChatMessage, g: TurnGroup) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
  msgImages?: (msg: UnifiedChatMessage | undefined) => ChatImageRef[];
  thumbOf?: (img: ChatImageRef) => string;
  imgLost?: (img: ChatImageRef) => boolean;
  liveSegs?: DisplaySeg[];
  streamingText?: string;
  isGenerating?: boolean;
  streamMdKillSwitch?: boolean;
  pendingStatus?: string;
  /** need02-P3：轮末交接窗（model.liveCompleting 经 TimelineRow 透传；仅末轮为 true，见 isLast 门控）。 */
  liveCompleting?: boolean;
  /** need02-P3：交接动画总开关（p3；false = P2 硬切，直接切换）。 */
  handoffAnim?: boolean;
}>();
const emit = defineEmits<{
  toggle: [key: string];
  'user-menu': [e: MouseEvent];
  'assistant-menu': [e: MouseEvent];
  'link-click': [e: MouseEvent];
  'retry-pending': [];
}>();

const isFlat = computed(() => isSingleFlatTurn(props.turn));
/** need02-P3：交接窗有效性（readingWindow 期暂停交接动画，直接切终态，05 R11）。 */
const completingEffective = computed(() => !!props.liveCompleting && !props.readingWindow);
/** need02-P3：动画开关（p3 off = P2 硬切；内容交接仍正确，只无淡入淡出）。 */
const handoffAnimOn = computed(() => props.handoffAnim !== false);
/** 稳态可见性：非运行态恒可见；运行态仅在交接窗/阅读窗内可见（与 LiveBody 共存或替代）。 */
const showSteady = computed(() => !props.turn.isRunning || completingEffective.value || !!props.readingWindow);
const lastContent = computed(() => lastStepContent(props.turn));
const earlier = computed<UserTurnStep[]>(() => earlierSteps(props.turn));
const liveSegs = computed<DisplaySeg[]>(() => (Array.isArray(props.liveSegs) ? props.liveSegs : []));
const streamingText = computed(() => String(props.streamingText || ''));
const renderHtmlFn = (t: string): string => {
  try {
    return props.renderHtml(t);
  } catch {
    return String(t || '');
  }
};
const renderStreamFn = (t: string): string => {
  try {
    if (props.renderStream) return props.renderStream(t);
    return renderHtmlFn(t);
  } catch {
    return String(t || '');
  }
};
const userImgs = computed<ChatImageRef[]>(() => {
  try {
    if (props.msgImages && props.turn.userMsg) return props.msgImages(props.turn.userMsg) || [];
    const raw = (props.turn.userMsg as unknown as { images?: ChatImageRef[] } | null)?.images;
    return Array.isArray(raw) ? raw : [];
  } catch {
    return [];
  }
});
function thumbOfWrap(img: ChatImageRef): string {
  try {
    return props.thumbOf ? String(props.thumbOf(img) || '') : '';
  } catch {
    return '';
  }
}
function imgLostWrap(img: ChatImageRef): boolean {
  try {
    if (props.imgLost) return !!props.imgLost(img);
    return (img as ChatImageRef).lost === true;
  } catch {
    return false;
  }
}
function groupOf(st: UserTurnStep): TurnGroup {
  return {
    key: String(st.assistantId || `step-${st.iteration}`),
    assistantId: String(st.assistantId || ''),
    reasoning: st.reasoning,
    content: st.content,
    toolCalls: (st.toolCalls || []).map((t) => ({ id: String(t.id || ''), name: String(t.name || 'tool') })),
    toolMessages: st.toolMessages || [],
    telemetry: st.telemetry || null,
    createdAtUtc: String(st.createdAtUtc || ''),
  };
}
function cardForFn(m: UnifiedChatMessage, g: TurnGroup): AgentToolCard {
  return props.cardFor(m, g);
}
function toolKeyFn(m: UnifiedChatMessage): string {
  try {
    return String(props.toolKey(m) || (m as UnifiedChatMessage).id || '');
  } catch {
    return String((m as UnifiedChatMessage).id || '');
  }
}
const pendingStatus = computed(() => String(props.pendingStatus || ''));
</script>
<style scoped>
.msg-thumb-slot {
  width: 96px;
  height: 96px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  overflow: hidden;
}
.msg-thumb-skeleton {
  border-radius: 4px;
  border: 1px solid var(--border-subtle);
  background: linear-gradient(90deg, #8881 25%, #8883 50%, #8881 75%);
  background-size: 200% 100%;
  animation: thumb-shimmer 1.2s linear infinite;
}
@keyframes thumb-shimmer {
  to {
    background-position: -200% 0;
  }
}
@media (prefers-reduced-motion: reduce) {
  .msg-thumb-skeleton {
    animation: none;
  }
}
.pending-spinner {
  width: 11px;
  height: 11px;
  border-radius: 9999px;
  border: 1.5px solid var(--border-subtle);
  border-top-color: var(--text-secondary);
  animation: pending-spin 0.9s linear infinite;
  display: inline-block;
}
@keyframes pending-spin {
  to {
    transform: rotate(360deg);
  }
}
@media (prefers-reduced-motion: reduce) {
  .pending-spinner {
    animation: none;
  }
}
.pending-retry-btn {
  font-size: 11px;
  line-height: 1;
  padding: 3px 8px;
  border-radius: 9999px;
  border: 1px solid #f87171;
  color: #ef4444;
  background: transparent;
  cursor: pointer;
}
</style>
