<template>
  <div :ref="anchor(item.key)" data-testid="live-row">
    <UserTurnLiveBody :segs="liveSegs" :is-generating="generating" @link-click="emit('link-click', $event)" />
    <TimelineAlerts
      v-if="isLast && noticeOn && (noticeText || lastErrorText)"
      :notice="noticeText"
      :error="lastErrorText"
      :show-retry="retryOn"
      @retry="emit('retry')"
      @dismiss="emit('dismiss')"
    />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import UserTurnLiveBody from './userturn/UserTurnLiveBody.vue';
import TimelineAlerts from './timeline/TimelineAlerts.vue';
import { timelineViewFallback, useTimelineViewOptional } from '@features/agent/composables/timelineViewKey';

/** Live 专用行：直挂流段，不复用轮容器。 */
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

const liveSegs = computed(() => view.value.liveSegsFor(props.item));
const generating = computed(() => view.value.isGenerating);

const noticeOn = computed(() => view.value.noticeInTurn);
const noticeText = computed(() => view.value.turnNotice);
const lastErrorText = computed(() => view.value.lastError);
const retryOn = computed(() => view.value.showRetry);
</script>
