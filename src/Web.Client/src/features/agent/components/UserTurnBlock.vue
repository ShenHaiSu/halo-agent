<template>
  <div class="turn-body turn-single-container min-w-0" data-testid="user-turn-block" :data-turn-key="turn.key">
    <div v-if="turn.userMsg" class="flex justify-end turn-user-slot">
      <UserBubble
        :content="String(turn.userMsg.content ?? '')"
        :images="userImgs"
        :pending-status="pendingStatus"
        @user-menu="emit('user-menu', $event)"
        @retry-pending="emit('retry-pending')"
      />
    </div>

    <UserTurnTransition :running="turn.isRunning && !readingWindow" :completing="completingEffective" :anim-on="handoffAnimOn">
      <template #live>
        <UserTurnLiveBody :segs="liveSegs" :is-generating="!!isGenerating" @link-click="emit('link-click', $event)" />
      </template>
      <template #default>
        <UserTurnSteady
          v-if="showSteady && isFlat"
          :last-content="lastContent"
          :is-flat="true"
          @link-click="emit('link-click', $event)"
          @assistant-menu="emit('assistant-menu', $event)"
        />
        <UserTurnProcessBody
          v-else-if="showSteady"
          :turn="turn"
          :expanded="expanded"
          :earlier="earlier"
          :tail-segs="tailSegs"
          :last-content="lastContent"
          :is-generating="!!isGenerating"
          @toggle="emit('toggle', $event)"
          @link-click="emit('link-click', $event)"
          @assistant-menu="emit('assistant-menu', $event)"
        />
      </template>
    </UserTurnTransition>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { ChatImageRef } from '@shared/bridge/hostBridge';
import { earlierSteps, isSingleFlatTurn, lastStepContent, type UserTurn } from '@features/session/utils/userTurn';
import { pendingTailSlot } from '@features/session/utils/turnRender';
import UserBubble from './userturn/UserBubble.vue';
import UserTurnLiveBody from './userturn/UserTurnLiveBody.vue';
import UserTurnSteady from './userturn/UserTurnSteady.vue';
import UserTurnProcessBody from './userturn/UserTurnProcessBody.vue';
import UserTurnTransition from './UserTurnTransition.vue';
import { timelineViewFallback, useTimelineViewOptional, type TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';

/** 用户轮装配器：气泡 + 流态/稳态分支，只保留五个派生计算，其余经视图模型取用。 */
const props = defineProps<{
  turn: UserTurn;
  expanded: boolean;
  liveSegs?: TimelineLiveSeg[];
  isGenerating?: boolean;
  pendingStatus?: string;
  liveCompleting?: boolean;
  handoffAnim?: boolean;
  singleProcess?: boolean;
}>();

const emit = defineEmits<{
  toggle: [key: string];
  'user-menu': [e: MouseEvent];
  'assistant-menu': [e: MouseEvent];
  'link-click': [e: MouseEvent];
  'retry-pending': [];
}>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());
const readingWindow = computed(() => view.value.readingWindow);

const isFlat = computed(() => isSingleFlatTurn(props.turn));
const completingEffective = computed(() => !!props.liveCompleting && !readingWindow.value);
const handoffAnimOn = computed(() => props.handoffAnim !== false);
const showSteady = computed(() => !props.turn.isRunning || completingEffective.value || readingWindow.value);
const lastContent = computed(() => lastStepContent(props.turn));
const earlier = computed(() => earlierSteps(props.turn));
const liveSegs = computed<TimelineLiveSeg[]>(() => (Array.isArray(props.liveSegs) ? props.liveSegs : []));

const singleProcessOn = computed(() => props.singleProcess ?? view.value.singleProcess);
const tailSegs = computed<TimelineLiveSeg[]>(() => {
  if (!singleProcessOn.value) return [];
  return pendingTailSlot(liveSegs.value, props.turn).map((s) => ({
    kind: String(s.kind || 'text'),
    key: String(s.key || ''),
    text: String(s.text || ''),
    card: (s.card ?? null) as TimelineLiveSeg['card'],
  }));
});

const userImgs = computed<ChatImageRef[]>(() => {
  try {
    if (props.turn.userMsg) return view.value.msgImages(props.turn.userMsg) || [];
    return [];
  } catch {
    const raw = (props.turn.userMsg as unknown as { images?: ChatImageRef[] } | null)?.images;
    return Array.isArray(raw) ? raw : [];
  }
});

const pendingStatus = computed(() => String(props.pendingStatus || ''));
</script>

<style scoped>
/* 单轮单容器三段：user 上 / 过程中 / 末段下恒定，流稳同槽，毕业不搬家。 */
.turn-single-container {
  display: flex;
  flex-direction: column;
  content-visibility: auto;
  contain-intrinsic-size: auto 240px;
}
.turn-user-slot {
  order: 1;
}
.uturn-handoff {
  order: 2;
}
.turn-steady-stack {
  display: flex;
  flex-direction: column;
}
.turn-process-slot {
  order: 1;
}
.turn-earlier-slot {
  order: 2;
}
.turn-final-slot {
  order: 3;
}
</style>
