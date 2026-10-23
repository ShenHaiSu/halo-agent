<template>
  <div v-if="item.kind === 'user'" :ref="anchorRef(item.key)" class="flex justify-end">
    <div
      class="max-w-[85%] rounded-lg bg-elevated border border-subtle px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
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
    </div>
  </div>
  <div v-else-if="item.kind === 'system'" :ref="anchorRef(item.key)" class="text-[11px] text-secondary text-center font-mono px-4">
    {{ item.msg?.content }}
  </div>
  <div v-else :ref="anchorRef(item.key)">
    <TurnGroupItem
      v-if="item.group"
      :group="item.group"
      :expanded="isExpanded(item.group.key || item.key)"
      :has-failed="hasFailed(item.group)"
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
import type { ComponentPublicInstance } from 'vue';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import TurnGroupItem from './TurnGroupItem.vue';
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
  showTelemetry: (g: TurnGroup) => boolean;
  protocolOf: (g: TurnGroup) => string;
  cacheLineOf: (g: TurnGroup) => string;
  /** M1-4：单图丢失判定（调用方提供精确判定时优先；缺省回落到 img.lost）。 */
  imgLost?: (img: ChatImageRef) => boolean;
}>();
/** M1-4：单图丢失判定——决定渲染「标签槽位」还是「加载骨架」。 */
function isImgLost(img: ChatImageRef): boolean {
  if (props.imgLost) return props.imgLost(img);
  return img.lost === true;
}
const emit = defineEmits<{
  toggle: [key: string];
  'user-menu': [e: MouseEvent, item: TimelineEntry];
  'assistant-menu': [e: MouseEvent, item: TimelineEntry];
  'link-click': [e: MouseEvent];
  'content-menu': [e: MouseEvent];
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
</style>
