<template>
  <div v-if="item.kind === 'user'" :ref="anchorRef(item.key)" class="flex justify-end">
    <div
      class="max-w-[85%] rounded-lg bg-elevated border px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
      :class="pendingCls"
      @contextmenu.prevent="emit('user-menu', $event, item)"
    >
      <template v-for="(part, pi) in userParts(String(item.msg?.content ?? ''))" :key="pi">
        <span v-if="part.type === 'text'">{{ part.text }}</span>
        <SkillPillChip v-else :scope="part.scope" :name="part.name" class="inline-chip" />
      </template>
      <div v-if="msgImages(item.msg).length" class="mt-1.5 flex flex-wrap gap-1.5" data-testid="msg-images">
        <template v-for="img in msgImages(item.msg)" :key="img.imageId">
          <n-image
            v-if="thumbOf(img)"
            :src="thumbOf(img)"
            :alt="img.fileName"
            width="96"
            height="96"
            object-fit="cover"
            class="rounded border msg-thumb-slot"
            :data-testid="`msg-image-${img.imageId}`"
            @load="markImgLoaded"
            @error="markImgLoaded"
          />
          <n-tag v-else-if="isImgLost(img)" size="small" :bordered="true" :title="img.fileName" class="msg-thumb-slot">
            {{ img.fileName }}
          </n-tag>
          <div v-else class="msg-thumb-slot msg-thumb-skeleton" :title="img.fileName" aria-busy="true" />
        </template>
      </div>
      <div v-if="hasLost(item.msg)" class="text-[11px] text-amber-500">部分图片已丢失（文件被删除）</div>
      <div
        v-if="pendingState === 'sending'"
        data-testid="pending-sending"
        class="mt-1 flex items-center justify-end gap-1 text-[11px] text-secondary select-none"
      >
        <span class="pending-spinner" aria-hidden="true" />
        <span>发送中…</span>
      </div>
      <div
        v-else-if="pendingState === 'failed'"
        data-testid="pending-failed"
        class="mt-1 flex items-center justify-end gap-1.5 text-[11px] text-red-500 select-none"
      >
        <span>发送失败·点击重发</span>
        <button type="button" class="pending-retry-btn" data-testid="pending-retry-btn" @click.stop="emit('retry-pending', item)">
          重发
        </button>
      </div>
    </div>
  </div>
  <div v-else-if="item.kind === 'system'" :ref="anchorRef(item.key)" class="text-[11px] text-secondary text-center font-mono px-4">
    {{ item.msg?.content }}
  </div>
  <div v-else-if="item.kind === 'user-turn'" :ref="anchorRef(item.key)">
    <UserTurnBlock
      v-if="item.turn"
      :turn="item.turn"
      :expanded="isExpanded(item.turn.key || item.key)"
      :reading-window="!!readingWindow"
      :render-html="renderHtml"
      :render-stream="renderStreamFn"
      :card-for="cardFor"
      :tool-key="toolKey"
      :msg-images="msgImages"
      :thumb-of="thumbOf"
      :img-lost="imgLost"
      :live-segs="liveSegsValue"
      :is-generating="!!isGeneratingValue"
      :stream-md-kill-switch="!!streamMdKillSwitch"
      :pending-status="pendingStatusOfTurn"
      :live-completing="!!liveCompleting && !!isLast"
      :handoff-anim="handoffAnim !== false"
      :single-process="singleProcess !== false"
      @toggle="emit('toggle', $event)"
      @user-menu="(e) => emit('user-menu', e, item)"
      @assistant-menu="(e) => emit('assistant-menu', e, item)"
      @link-click="emit('link-click', $event)"
      @retry-pending="emit('retry-pending', item)"
    />
    <div v-if="!!isLast && !!noticeInTurn && (turnNotice || lastError)" class="mt-1.5 space-y-1.5" data-testid="user-turn-notice">
      <n-alert v-if="turnNotice" type="info" :show-icon="false" class="text-xs">{{ turnNotice }}</n-alert>
      <n-alert v-if="lastError" type="error" class="text-xs" data-testid="turn-error-alert">{{ lastError }}</n-alert>
      <div v-if="showRetry" class="flex items-center gap-2">
        <n-button size="small" type="error" ghost data-testid="turn-retry-btn" @click="emit('retry')">重试本轮</n-button>
        <n-button size="small" quaternary data-testid="turn-error-dismiss" @click="emit('dismiss')">忽略</n-button>
      </div>
    </div>
  </div>
  <div
    v-if="!!isLast && !!noticeInTurn && (turnNotice || lastError) && item.kind !== 'user-turn'"
    class="mt-1.5 space-y-1.5"
    data-testid="user-turn-notice"
  >
    <n-alert v-if="turnNotice" type="info" :show-icon="false" class="text-xs">{{ turnNotice }}</n-alert>
    <n-alert v-if="lastError" type="error" class="text-xs" data-testid="turn-error-alert">{{ lastError }}</n-alert>
    <div v-if="showRetry" class="flex items-center gap-2">
      <n-button size="small" type="error" ghost data-testid="turn-retry-btn" @click="emit('retry')">重试本轮</n-button>
      <n-button size="small" quaternary data-testid="turn-error-dismiss" @click="emit('dismiss')">忽略</n-button>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import type { ComponentPublicInstance } from 'vue';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import UserTurnBlock from './UserTurnBlock.vue';
import SkillPillChip from './SkillPillChip.vue';
import { userParts } from '../utils/userParts';
const props = defineProps<{
  item: TimelineEntry;
  isExpanded: (key: string) => boolean;
  anchorRef: (key: string) => (el: Element | ComponentPublicInstance | null) => void;
  msgImages: (msg: UnifiedChatMessage | undefined) => ChatImageRef[];
  thumbOf: (img: ChatImageRef) => string;
  hasLost: (msg: UnifiedChatMessage | undefined) => boolean;
  renderHtml: (text: string) => string;
  cardFor: (m: UnifiedChatMessage, g: TurnGroup) => AgentToolCard;
  toolKey: (m: UnifiedChatMessage) => string;
  imgLost?: (img: ChatImageRef) => boolean;
  readingWindow?: boolean;
  isPending?: (msg: UnifiedChatMessage | undefined) => boolean;
  pendingStatus?: (msg: UnifiedChatMessage | undefined) => string;
  liveSegs?: Array<{ kind: string; key: string; text: string; card: unknown }>;
  isGenerating?: boolean;
  streamMdKillSwitch?: boolean;
  renderStream?: (text: string) => string;
  isLast?: boolean;
  liveCompleting?: boolean;
  handoffAnim?: boolean;
  singleProcess?: boolean;
  noticeInTurn?: boolean;
  turnNotice?: string;
  lastError?: string;
  showRetry?: boolean;
  hasFailed?: (g: TurnGroup) => boolean;
  failedCount?: (g: TurnGroup) => number;
  showTelemetry?: (g: TurnGroup) => boolean;
  protocolOf?: (g: TurnGroup) => string;
  cacheLineOf?: (g: TurnGroup) => string;
  isRunExpanded?: (key: string) => boolean;
}>();

function isImgLost(img: ChatImageRef): boolean {
  if (props.imgLost) return props.imgLost(img);
  return img.lost === true;
}

function markImgLoaded(e: Event): void {
  try {
    const t = e.target as HTMLElement | null;
    t?.setAttribute?.('data-loaded', 'true');
  } catch {
    /* ignore */
  }
}

const pendingState = computed<string>(() => {
  try {
    if (props.pendingStatus) return String(props.pendingStatus(props.item.msg) || '');
    const id = String(props.item.msg?.id || '');
    return id.startsWith('pending-') ? 'sending' : '';
  } catch {
    return '';
  }
});

const pendingStatusOfTurn = computed<string>(() => {
  try {
    const t = (props.item as unknown as { turn?: { userMsg?: UnifiedChatMessage } }).turn;
    const um = t?.userMsg;
    if (!um) return '';
    if (props.pendingStatus) return String(props.pendingStatus(um) || '');
    const id = String(um.id || '');
    return id.startsWith('pending-') ? 'sending' : '';
  } catch {
    return '';
  }
});
const liveSegsValue = computed(() => {
  try {
    return (props.liveSegs || []) as never[];
  } catch {
    return [];
  }
});
const isGeneratingValue = computed(() => props.isGenerating === true);
const renderStreamFn = (t: string): string => {
  try {
    if (props.renderStream) return String(props.renderStream(t) || '');
  } catch {
    /* fallthrough */
  }
  try {
    return String(props.renderHtml(t) || '');
  } catch {
    return String(t || '');
  }
};
const isPendingMsg = computed<boolean>(() => {
  try {
    if (props.isPending) return !!props.isPending(props.item.msg);
    return String(props.item.msg?.id || '').startsWith('pending-');
  } catch {
    return false;
  }
});
void isPendingMsg.value;
const pendingCls = computed<string>(() => {
  if (pendingState.value === 'failed') return 'border-red-400 pending-failed-bubble';
  return 'border-subtle';
});
const emit = defineEmits<{
  toggle: [key: string];
  'user-menu': [e: MouseEvent, item: TimelineEntry];
  'assistant-menu': [e: MouseEvent, item: TimelineEntry];
  'link-click': [e: MouseEvent];
  'content-menu': [e: MouseEvent];
  'retry-pending': [item: TimelineEntry];
  retry: [];
  dismiss: [];
}>();
void emit;
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
.pending-failed-bubble {
  border-color: #f87171 !important;
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
.pending-retry-btn:hover {
  background: #fef2f2;
}
</style>
