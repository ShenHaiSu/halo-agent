<template>
  <div class="turn-body turn-steady-stack min-w-0">
    <UserTurnSummary
      class="turn-process-slot"
      :turn="turn"
      :expanded="expanded"
      :reading-window="readingWindow"
      @toggle="emit('toggle', $event)"
    />
    <n-collapse-transition class="turn-earlier-slot" :show="expanded">
      <div
        class="turn-body process-body px-3 pb-2 max-h-72 overflow-y-auto min-w-0 rounded-r-md bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70"
        data-testid="process-body"
      >
        <div
          v-for="(st, i) in earlier"
          :key="st.assistantId || `step-${i}`"
          class="min-w-0"
          :data-testid="`user-turn-step-${i + 1}`"
          :data-anchor-key="stepAnchor(st)"
        >
          <div class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] select-none">
            <span class="font-mono shrink-0 opacity-70">{{ st.iteration }}</span>
            <span class="truncate">第 {{ st.iteration }} 步</span>
            <span v-if="st.reasoning" class="shrink-0 opacity-60">· 思考</span>
            <span v-if="(st.toolMessages?.length || 0) > 0" class="shrink-0 font-mono opacity-60"
              >· {{ st.toolMessages.length }} 个工具</span
            >
          </div>
          <div
            v-if="st.content"
            class="mt-1 app-text-body leading-relaxed text-primary break-words select-text min-w-0"
            @click="emit('link-click', $event)"
            v-html="renderHtml(st.content)"
          />
          <div
            v-if="st.reasoning"
            class="mt-1 whitespace-pre-wrap break-words select-text app-text-small text-[var(--reasoning-text)] max-h-[40vh] overflow-y-auto"
          >
            {{ st.reasoning }}
          </div>
          <div v-if="(st.toolMessages?.length || 0) > 0" class="turn-body mt-1 min-w-0">
            <ToolCallCard v-for="t in st.toolMessages" :key="cardKey(t)" :card="cardFor(t, stepGroup(st))" :default-collapsed="true" />
          </div>
        </div>
        <div v-if="showTail" class="turn-body min-w-0" data-testid="pending-tail">
          <div v-for="(seg, i) in tailSegs" :key="seg.key" :data-testid="`pending-tail-seg-${i}`">
            <StreamSegItem
              :seg="seg"
              :stream-md-kill-switch="killSwitch"
              :render-stream="renderStream"
              :is-tail="i === tailSegs.length - 1"
              :is-generating="isGenerating"
              @link-click="emit('link-click', $event)"
            />
          </div>
        </div>
      </div>
    </n-collapse-transition>
    <UserTurnSteady
      :last-content="lastContent"
      :is-flat="false"
      @link-click="emit('link-click', $event)"
      @assistant-menu="emit('assistant-menu', $event)"
    />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { UnifiedChatMessage } from '@shared/bridge/hostBridge';
import type { UserTurn, UserTurnStep } from '@features/session/utils/userTurn';
import { groupOfStep, turnStepAnchorKey } from '@features/session/utils/turnRender';
import UserTurnSummary from '@features/agent/components/UserTurnSummary.vue';
import StreamSegItem from '@features/agent/components/StreamSegItem.vue';
import ToolCallCard from '@features/agent/components/ToolCallCard.vue';
import UserTurnSteady from './UserTurnSteady.vue';
import { timelineViewFallback, useTimelineViewOptional, type TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';

/** 稳态多步体：上折叠行 + 早轮列表 + 轮内流式尾槽 + 末段直出，工具卡经视图模型派生。 */
const props = defineProps<{
  turn: UserTurn;
  expanded: boolean;
  earlier: UserTurnStep[];
  tailSegs: TimelineLiveSeg[];
  lastContent: string;
  isGenerating: boolean;
}>();

const emit = defineEmits<{
  toggle: [key: string];
  'link-click': [e: MouseEvent];
  'assistant-menu': [e: MouseEvent];
}>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());
const readingWindow = computed(() => view.value.readingWindow);
const killSwitch = computed(() => view.value.streamMdKillSwitch);
const showTail = computed(() => view.value.singleProcess && props.tailSegs.length > 0);

function stepAnchor(st: UserTurnStep): string {
  return turnStepAnchorKey(props.turn.key, st.iteration);
}

function stepGroup(st: UserTurnStep) {
  return groupOfStep(props.turn.key, st);
}

function renderHtml(t: string): string {
  try {
    return view.value.renderHtml(t);
  } catch {
    return String(t || '');
  }
}

function renderStream(t: string): string {
  try {
    return view.value.renderStream(t);
  } catch {
    return String(t || '');
  }
}

function cardFor(m: UnifiedChatMessage, g: ReturnType<typeof groupOfStep>) {
  return view.value.cardFor(m, g);
}

function cardKey(m: UnifiedChatMessage): string {
  try {
    return String(view.value.toolKey(m) || m.id || '');
  } catch {
    return String(m.id || '');
  }
}
</script>
