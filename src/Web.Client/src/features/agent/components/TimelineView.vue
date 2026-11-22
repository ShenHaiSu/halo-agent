<template>
  <div class="flex-1 min-h-0 min-w-0 max-w-full relative flex flex-col" data-testid="message-timeline" :data-need02="need02Attr">
    <div ref="scroll" class="flex-1 min-h-0 overflow-y-auto min-w-0 max-w-full overflow-x-hidden transcript-scroll">
      <div :class="chatWidthMode === 'full' ? 'chat-timeline-full px-4 py-4 space-y-4' : 'chat-timeline-centered px-4 py-4 space-y-4'">
        <TimelineSummarySection
          v-if="summaryList.length > 0 || pendingUser"
          :summaries="summaryList"
          :pending="pendingUser"
          :handoff-active="showLiveHandoff"
          :handoff-final-text="handoffFinalText"
          @user-menu="onSummaryUserMenu"
          @assistant-menu="onSummaryAssistantMenu"
          @retry-pending="emit('retry-pending', liveList[0] ?? ({ key: 'live-pending', kind: 'live' } as TimelineEntry))"
        />
        <TimelineEmptyState v-if="isEmpty" />
        <template v-for="entry in liveList" :key="entry.key">
          <TurnHandoff
            v-if="showLiveHandoff"
            :segs="view.liveSegsFor(entry)"
            :completing="showLiveHandoff"
            :reduce-height="handoffReduceHeight"
            @handoff-done="onHandoffDone"
          />
          <TimelineRow v-else-if="!handoffActive" :item="entry" :is-last="true" v-on="rowHandlers" />
          <TimelineAlerts
            v-if="showLiveHandoff && noticeInTurn && (turnNotice || lastError)"
            :notice="turnNotice"
            :error="lastError"
            :show-retry="showRetry"
            @retry="emit('retry')"
            @dismiss="emit('dismiss')"
          />
        </template>
        <!-- 主链纯 V2：只留摘要区 + Live 条 + 全局区。 -->
        <n-alert v-if="turnNotice && !noticeInTurn" type="info" :show-icon="false" class="text-xs">
          {{ turnNotice }}
        </n-alert>
        <n-alert v-if="lastError && !noticeInTurn" type="error" class="text-xs" data-testid="turn-error-alert">
          {{ lastError }}
        </n-alert>
        <div v-if="showRetry && !noticeInTurn" class="flex items-center gap-2">
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
          :visible="menu.visible"
          :x="menu.x"
          :y="menu.y"
          :busy="menu.busy"
          :can-copy="menu.canCopy"
          :can-copy-turn="menu.canCopyTurn"
          :copy-label="menu.copyLabel"
          @copy="emit('menu-copy')"
          @copy-turn="emit('menu-copy-turn')"
          @replay="emit('menu-replay')"
          @branch="emit('menu-branch')"
          @delete="emit('menu-delete')"
        />
      </div>
    </div>
    <TimelineJumpButton :visible="showJumpVisible" @jump="emit('jump-latest')" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, useTemplateRef, watch } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import TimelineRow from './TimelineRow.vue';
import TurnHandoff from './userturn/TurnHandoff.vue';
import TimelineAlerts from './timeline/TimelineAlerts.vue';
import TimelineEmptyState from './timeline/TimelineEmptyState.vue';
import TimelineJumpButton from './timeline/TimelineJumpButton.vue';
import TimelineSummarySection from '@features/session/components/TimelineSummarySection.vue';
import MessageContextMenu from './MessageContextMenu.vue';
import { timelineViewFallback, useTimelineViewOptional } from '@features/agent/composables/timelineViewKey';
import { useWorkspaceStore } from '@/stores/workspaceStore';

/** 滚动容器 + 摘要区 + Live 条 + 全局区；行内派生全部经视图模型取用。 */
const props = defineProps<{
  isEmpty?: boolean;
  showJumpBtn?: boolean;
  readingWindow?: boolean;
  chatWidthMode?: string;
  need02Attr?: string;
}>();

const emit = defineEmits<{
  ready: [el: HTMLElement | null];
  'user-menu': [e: MouseEvent, item: TimelineEntry];
  'assistant-menu': [e: MouseEvent, item: TimelineEntry];
  'link-click': [e: MouseEvent];
  retry: [];
  dismiss: [];
  'menu-copy': [];
  'menu-copy-turn': [];
  'menu-replay': [];
  'menu-branch': [];
  'menu-delete': [];
  'jump-latest': [];
  'retry-pending': [item: TimelineEntry];
}>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());

const summaryList = computed(() => view.value.summaryList ?? []);
const pendingUser = computed(() => view.value.pendingUser ?? null);
const hasActiveTurn = computed(() => view.value.hasActiveTurn);

// 交接窗：正文全程单份且满高。收缩完成后原子换手——撤 Live 容器、显稳态正文，两件事同一帧。
const handoffActive = computed(() => !!view.value.handoffActive);
const handoffFinalText = computed(() => view.value.handoffFinalText || '');
const handoffReduceHeight = computed(() => !!view.value.handoffReduceHeight);
const handoffDone = ref(false);
watch(handoffActive, (v) => {
  if (!v) handoffDone.value = false;
});
const showLiveHandoff = computed(() => handoffActive.value && !handoffDone.value);
function onHandoffDone(): void {
  handoffDone.value = true;
}

// 直播挂载不需要轮容器：有活跃轮即新建 live 兜底条目直挂流段，key 绑定会话切会话即换。
const liveList = computed<TimelineEntry[]>(() => {
  if (!hasActiveTurn.value) return [];
  const sid = String(useWorkspaceStore().activeSessionId || 'active');
  return [{ key: `live-${sid}`, kind: 'live' }];
});

const noticeInTurn = computed(() => view.value.noticeInTurn);
const turnNotice = computed(() => view.value.turnNotice);
const lastError = computed(() => view.value.lastError);
const showRetry = computed(() => view.value.showRetry);
const liveTelemetry = computed(() => view.value.liveTelemetry);
const menu = computed(() => view.value.menu);

const rowHandlers = {
  'user-menu': (e: MouseEvent, item: TimelineEntry) => emit('user-menu', e, item),
  'assistant-menu': (e: MouseEvent, item: TimelineEntry) => emit('assistant-menu', e, item),
  'link-click': (e: MouseEvent) => emit('link-click', e),
  'retry-pending': (item: TimelineEntry) => emit('retry-pending', item),
  retry: () => emit('retry'),
  dismiss: () => emit('dismiss'),
};

/**
 * V2 摘要区右键桥接：纯转发 turnId，菜单内容组装走 useTimelineRowActions（摘要源唯一组装点）。
 */
async function onSummaryUserMenu(ev: MouseEvent, turnId: string): Promise<void> {
  rowHandlers['user-menu'](ev, {
    kind: 'live',
    key: `live-${turnId}`,
  } as unknown as TimelineEntry);
}

async function onSummaryAssistantMenu(ev: MouseEvent, turnId: string): Promise<void> {
  rowHandlers['assistant-menu'](ev, {
    kind: 'live',
    key: `live-${turnId}`,
  } as unknown as TimelineEntry);
}

const showJumpVisible = computed(() => !!props.showJumpBtn || !!props.readingWindow);

const scroll = useTemplateRef<HTMLElement>('scroll');
onMounted(() => emit('ready', scroll.value));
onUnmounted(() => emit('ready', null));
</script>

<style scoped>
/* 滚动锚定与滚动条槽位：配合渐进跟随才真正生效，单独声明时只铺声明。 */
.transcript-scroll {
  overflow-anchor: auto;
  scrollbar-gutter: stable;
}
</style>
