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
          <n-image
            v-if="thumbOf(img)"
            :src="thumbOf(img)"
            :alt="img.fileName"
            width="96"
            object-fit="cover"
            class="rounded border"
            :data-testid="`msg-image-${img.imageId}`"
          />
          <n-tag v-else size="small" :bordered="true" :title="img.fileName">
            {{ img.fileName }}
          </n-tag>
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

defineProps<{
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
}>();
const emit = defineEmits<{
  toggle: [key: string];
  'user-menu': [e: MouseEvent, item: TimelineEntry];
  'assistant-menu': [e: MouseEvent, item: TimelineEntry];
  'link-click': [e: MouseEvent];
  'content-menu': [e: MouseEvent];
}>();
void emit;
</script>
