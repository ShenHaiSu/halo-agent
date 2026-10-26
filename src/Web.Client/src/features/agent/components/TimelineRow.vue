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
          <!-- 已加载：恒定 96×96 槽位，缩略图到达后原位替换，不改变高度 -->
          <n-image
            v-if="thumbOf(img)"
            :src="thumbOf(img)"
            :alt="img.fileName"
            width="96"
            height="96"
            object-fit="cover"
            class="rounded border msg-thumb-slot"
            :data-testid="`msg-image-${img.imageId}`"
          />
          <!-- 丢失：保留原有标签语义，但占同一尺寸槽位 -->
          <n-tag v-else-if="isImgLost(img)" size="small" :bordered="true" :title="img.fileName" class="msg-thumb-slot">
            {{ img.fileName }}
          </n-tag>
          <!-- 加载中：占位骨架，尺寸与 n-image 一致 → 零高度跳变 -->
          <div v-else class="msg-thumb-slot msg-thumb-skeleton" :title="img.fileName" aria-busy="true" />
        </template>
      </div>
      <div v-if="hasLost(item.msg)" class="text-[11px] text-amber-500">部分图片已丢失（文件被删除）</div>
      <!-- need02-P1：乐观 user 态（右下小字，不占高度跳变） -->
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
        <button
          type="button"
          class="pending-retry-btn"
          data-testid="pending-retry-btn"
          @click.stop="emit('retry-pending', item)"
        >
          重发
        </button>
      </div>
    </div>
  </div>
  <div v-else-if="item.kind === 'system'" :ref="anchorRef(item.key)" class="text-[11px] text-secondary text-center font-mono px-4">
    {{ item.msg?.content }}
  </div>
  <!-- need-01：无正文轮次合并舱（独立 kind，显式分支；不影响任何 item.group 断言） -->
  <div v-else-if="item.kind === 'turn-run'" :ref="anchorRef(item.key)">
    <TurnRunItem
      v-if="item.run"
      :run="item.run"
      :expanded="isExpanded(item.run.key || item.key)"
      :reading-window="!!readingWindow"
      :card-for="cardFor"
      :tool-key="toolKey"
      @toggle="emit('toggle', $event)"
    />
  </div>
  <!-- need02-P2：用户轮次块（D1=B；p2 off 时无此 kind，回旧链）。 -->
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
      :streaming-text="streamingTextValue"
      :is-generating="!!isGeneratingValue"
      :stream-md-kill-switch="!!streamMdKillSwitch"
      :pending-status="pendingStatusOfTurn"
      @toggle="emit('toggle', $event)"
      @user-menu="(e) => emit('user-menu', e, item)"
      @assistant-menu="(e) => emit('assistant-menu', e, item)"
      @link-click="emit('link-click', $event)"
      @retry-pending="emit('retry-pending', item)"
    />
  </div>
  <div v-else :ref="anchorRef(item.key)">
    <TurnGroupItem
      v-if="item.group"
      :group="item.group"
      :expanded="isExpanded(item.group.key || item.key)"
      :has-failed="hasFailed(item.group)"
      :failed-count="failedCount?.(item.group)"
      :show-telemetry="showTelemetry(item.group)"
      :protocol="protocolOf(item.group)"
      :cache-line="cacheLineOf(item.group)"
      :render-html="renderHtml"
      :card-for="(m) => cardFor(m, item.group!)"
      :tool-key="toolKey"
      @toggle="emit('toggle', $event)"
      @content-menu="emit('assistant-menu', $event, item)"
      @link-click="emit('link-click', $event)"
    />
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue';
import type { ComponentPublicInstance } from 'vue';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import TurnGroupItem from './TurnGroupItem.vue';
import TurnRunItem from './TurnRunItem.vue';
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
  hasFailed: (g: TurnGroup) => boolean;
  /** 该轮失败 tool 条数；缺省时 TurnGroupItem 回落到 hasFailed 的布尔语义（1 个红点）。 */
  failedCount?: (g: TurnGroup) => number;
  showTelemetry: (g: TurnGroup) => boolean;
  protocolOf: (g: TurnGroup) => string;
  cacheLineOf: (g: TurnGroup) => string;
  /** M1-4：单图丢失判定（调用方提供精确判定时优先；缺省回落到 img.lost）。 */
  imgLost?: (img: ChatImageRef) => boolean;
  /** need-01：历史阅读窗口内不点亮合并舱的「运行中」态（与 useTimeline.readingWindow 同源）。 */
  readingWindow?: boolean;
  /** need02-P1：pending 判定/状态（缺省回落到 id 前缀，保证老调用方不炸）。 */
  isPending?: (msg: UnifiedChatMessage | undefined) => boolean;
  pendingStatus?: (msg: UnifiedChatMessage | undefined) => string;
  /** need02-P2：内嵌 LiveBody 用（仅运行轮消费；稳态轮忽略）。 */
  liveSegs?: Array<{ kind: string; key: string; text: string; card: unknown }>;
  streamingText?: string;
  isGenerating?: boolean;
  streamMdKillSwitch?: boolean;
  renderStream?: (text: string) => string;
}>();
/** M1-4：单图丢失判定——决定渲染「标签槽位」还是「加载骨架」。 */
function isImgLost(img: ChatImageRef): boolean {
  if (props.imgLost) return props.imgLost(img);
  return img.lost === true;
}
/** need02-P1：pending 态（sending/failed/''）。 */
const pendingState = computed<string>(() => {
  try {
    if (props.pendingStatus) return String(props.pendingStatus(props.item.msg) || '');
    const id = String(props.item.msg?.id || '');
    return id.startsWith('pending-') ? 'sending' : '';
  } catch {
    return '';
  }
});
/** need02-P2：user-turn 头的 pending 态（复用 pendingStatus(msg) 于 turn.userMsg）。 */
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
const streamingTextValue = computed(() => String(props.streamingText || ''));
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
void isPendingMsg;
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
}>();
void emit;
</script>
<style scoped>
/* M1-4：图片槽位恒定 96×96 —— 加载中/已加载/丢失三种状态占同一块空间，
   缩略图到达时只做原位替换，下方内容完全不移动（治 R6.3 的一次 +72px 跳变）。 */
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
/* need02-P1：pending 态只动 opacity，不做高度动画（05 R6）。 */
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
