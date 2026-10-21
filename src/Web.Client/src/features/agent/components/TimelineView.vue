<template>
  <div ref="scroll" class="flex-1 min-h-0 overflow-y-auto min-w-0 max-w-full overflow-x-hidden" data-testid="message-timeline">
    <div :class="chatWidthMode === 'full' ? 'chat-timeline-full px-4 py-4 space-y-4' : 'chat-timeline-centered px-4 py-4 space-y-4'">
      <div v-if="isEmpty" class="py-10 text-center space-y-2">
        <div class="flex justify-center">
          <Bot class="w-8 h-8 text-accent" />
        </div>
        <p class="text-sm text-primary font-medium">在下方输入任务指令，Agent 将自动编排工具完成</p>
        <p class="text-xs text-secondary font-mono">默认 Runner：CMD 优先 / PYTHON 次之 · 禁用 PowerShell 专属语法</p>
      </div>
      <template v-for="item in items" :key="item.key">
        <div v-if="item.kind === 'user'" :ref="anchorRef(item.key)" class="flex justify-end">
          <div
            class="max-w-[85%] rounded-lg bg-elevated border border-subtle px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
            @contextmenu.prevent="emit('user-menu', $event, item)"
          >
            {{ item.msg?.content }}
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
                <n-tag v-else size="small" :bordered="true" :title="img.fileName">{{ img.fileName }}</n-tag>
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
              style="white-space: pre-wrap"
              :data-seg="seg.key"
              @click="emit('link-click', $event)"
              v-html="renderStream(seg.text)"
            />
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
  </div>
</template>
<script setup lang="ts">
import { onMounted, onUnmounted, useTemplateRef, type ComponentPublicInstance } from 'vue';
import { Bot, BrainCircuit, Loader2 } from 'lucide-vue-next';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { TimelineEntry, TurnGroup } from '@features/session/utils/turnGroup';
import type { AgentToolCard } from '@features/agent/stores/model';
import type { DisplaySeg } from '../composables/useTimeline';
import ToolCallCard from './ToolCallCard.vue';
import MessageContextMenu from './MessageContextMenu.vue';
import TurnGroupItem from './TurnGroupItem.vue';

defineProps<{
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
}>();
const scroll = useTemplateRef<HTMLElement>('scroll');
onMounted(() => emit('ready', scroll.value));
onUnmounted(() => emit('ready', null));
</script>
<style scoped></style>
