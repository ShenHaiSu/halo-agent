<template>
  <div v-if="item.kind === 'user'" :ref="anchor(item.key)" class="flex justify-end">
    <UserBubble
      :content="String(item.msg?.content ?? '')"
      :images="userImages"
      :pending-status="userPending"
      @user-menu="(e) => emit('user-menu', e, item)"
      @retry-pending="emit('retry-pending', item)"
    />
  </div>
  <div v-else-if="item.kind === 'system'" :ref="anchor(item.key)" class="text-[11px] text-secondary text-center font-mono px-4">
    {{ item.msg?.content }}
  </div>
  <div v-else-if="item.kind === 'user-turn'" :ref="anchor(item.key)">
    <UserTurnBlock
      v-if="item.turn"
      :turn="item.turn"
      :expanded="turnExpanded"
      :live-segs="liveSegs"
      :is-generating="generating"
      :pending-status="turnPending"
      :live-completing="completing"
      :handoff-anim="handoff"
      :single-process="singleProc"
      @toggle="emit('toggle', $event)"
      @user-menu="(e) => emit('user-menu', e, item)"
      @assistant-menu="(e) => emit('assistant-menu', e, item)"
      @link-click="emit('link-click', $event)"
      @retry-pending="emit('retry-pending', item)"
    />
    <TimelineAlerts
      v-if="isLast && noticeOn && (noticeText || lastErrorText)"
      :notice="noticeText"
      :error="lastErrorText"
      :show-retry="retryOn"
      @retry="emit('retry')"
      @dismiss="emit('dismiss')"
    />
  </div>
  <TimelineAlerts
    v-if="isLast && noticeOn && (noticeText || lastErrorText) && item.kind !== 'user-turn'"
    :notice="noticeText"
    :error="lastErrorText"
    :show-retry="retryOn"
    @retry="emit('retry')"
    @dismiss="emit('dismiss')"
  />
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import UserBubble from './userturn/UserBubble.vue';
import UserTurnBlock from './UserTurnBlock.vue';
import TimelineAlerts from './timeline/TimelineAlerts.vue';
import { timelineViewFallback, useTimelineViewOptional } from '@features/agent/composables/timelineViewKey';

/** 行路由：user / system 直出，用户轮转装配器；派生一律经视图模型取用。 */
const props = defineProps<{ item: TimelineEntry; isLast?: boolean }>();

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

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());

function anchor(key: string) {
  return view.value.anchorRef(key);
}

const turnKey = computed(() => {
  if (props.item.kind !== 'user-turn') return String(props.item.key || '');
  const t = (props.item as unknown as { turn?: { key?: string } }).turn;
  return String(t?.key || props.item.key || '');
});

const userImages = computed(() => {
  if (props.item.kind !== 'user') return [];
  try {
    return view.value.msgImages(props.item.msg) || [];
  } catch {
    return [];
  }
});

const userPending = computed(() => {
  if (props.item.kind !== 'user') return '';
  try {
    return String(view.value.pendingStatus(props.item.msg) || '');
  } catch {
    return '';
  }
});

const turnPending = computed(() => {
  if (props.item.kind !== 'user-turn') return '';
  try {
    const um = (props.item as unknown as { turn?: { userMsg?: Parameters<typeof view.value.pendingStatus>[0] } }).turn?.userMsg;
    if (!um) return '';
    return String(view.value.pendingStatus(um) || '');
  } catch {
    return '';
  }
});

const turnExpanded = computed(() => view.value.isExpanded(turnKey.value));
const liveSegs = computed(() => view.value.liveSegsFor(props.item));
const generating = computed(() => view.value.isGenerating);
const completing = computed(() => view.value.liveCompleting && !!props.isLast);
const handoff = computed(() => view.value.handoffAnim);
const singleProc = computed(() => view.value.singleProcess);

const noticeOn = computed(() => view.value.noticeInTurn);
const noticeText = computed(() => view.value.turnNotice);
const lastErrorText = computed(() => view.value.lastError);
const retryOn = computed(() => view.value.showRetry);
</script>
