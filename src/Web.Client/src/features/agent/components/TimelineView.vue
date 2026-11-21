<template>
  <div class="flex-1 min-h-0 min-w-0 max-w-full relative flex flex-col" data-testid="message-timeline" :data-need02="need02Attr">
    <div ref="scroll" class="flex-1 min-h-0 overflow-y-auto min-w-0 max-w-full overflow-x-hidden transcript-scroll">
      <div :class="chatWidthMode === 'full' ? 'chat-timeline-full px-4 py-4 space-y-4' : 'chat-timeline-centered px-4 py-4 space-y-4'">
        <TimelineSummarySection
          v-if="summaryList.length > 0 || pendingUser"
          :summaries="summaryList"
          :pending="pendingUser"
          @user-menu="onSummaryUserMenu"
          @assistant-menu="onSummaryAssistantMenu"
          @retry-pending="emit('retry-pending', liveList[0] ?? ({ key: 'live-pending', kind: 'live' } as TimelineEntry))"
        />
        <TimelineEmptyState v-if="isEmpty" />
        <template v-for="entry in liveList" :key="entry.key">
          <TimelineRow :item="entry" :is-last="true" v-on="rowHandlers" />
        </template>
        <!-- 主链纯 V2：历史/尾部分片已下线（C3 停用，C4 删除文件），只留摘要区 + Live 条 + 全局区。 -->
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
          @copy="emit('menu-copy')"
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
import { computed, onMounted, onUnmounted, useTemplateRef } from 'vue';
import type { TimelineEntry } from '@features/session/utils/turnGroup';
import TimelineRow from './TimelineRow.vue';
import TimelineEmptyState from './timeline/TimelineEmptyState.vue';
import TimelineJumpButton from './timeline/TimelineJumpButton.vue';
import TimelineSummarySection from '@features/session/components/TimelineSummarySection.vue';
import MessageContextMenu from './MessageContextMenu.vue';
import { timelineViewFallback, useTimelineViewOptional } from '@features/agent/composables/timelineViewKey';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import type { TurnSummary } from '@shared/bridge/hostBridge';

/** 滚动容器 + 历史/尾部分片 + 全局区；行内派生全部经视图模型取用。 */
const props = defineProps<{
  items: TimelineEntry[];
  historyItems?: TimelineEntry[] | null;
  tailItems?: TimelineEntry[] | null;
  isEmpty?: boolean;
  showJumpBtn?: boolean;
  readingWindow?: boolean;
  chatWidthMode?: string;
  need02Attr?: string;
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
  'jump-latest': [];
  'retry-pending': [item: TimelineEntry];
}>();

const view = computed(() => useTimelineViewOptional() ?? timelineViewFallback());

const summaryList = computed(() => view.value.summaryList ?? []);
const pendingUser = computed(() => view.value.pendingUser ?? null);
const hasActiveTurn = computed(() => view.value.hasActiveTurn);

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
  toggle: (key: string) => emit('toggle', key),
  'user-menu': (e: MouseEvent, item: TimelineEntry) => emit('user-menu', e, item),
  'assistant-menu': (e: MouseEvent, item: TimelineEntry) => emit('assistant-menu', e, item),
  'link-click': (e: MouseEvent) => emit('link-click', e),
  'retry-pending': (item: TimelineEntry) => emit('retry-pending', item),
  retry: () => emit('retry'),
  dismiss: () => emit('dismiss'),
};

/** 按 turnId 从摘要列表取对应条目：V2 稳态下摘要是唯一携带原文的数据源。 */
function findSummary(turnId: string): TurnSummary | null {
  return summaryList.value.find((s) => String(s.turnId) === turnId) ?? null;
}

/**
 * V2 摘要区右键桥接：纯转发 turnId，菜单内容组装走摘要源，不再造 V1 兜底 entry。
 */
async function onSummaryUserMenu(ev: MouseEvent, turnId: string): Promise<void> {
  const summary = findSummary(turnId);
  if (!summary) return;
  const { summaryAnchorIds, summaryCopyText } = await import('@features/session/utils/summaryMenu');
  const anchors = summaryAnchorIds(summary);
  const text = summaryCopyText(summary, '');
  rowHandlers['user-menu'](ev, {
    kind: 'live',
    key: `live-${turnId}`,
    msg: { id: anchors.userId, content: text },
  } as unknown as TimelineEntry);
}

async function onSummaryAssistantMenu(ev: MouseEvent, turnId: string): Promise<void> {
  const summary = findSummary(turnId);
  if (!summary) return;
  const { summaryAnchorIds, summaryCopyText } = await import('@features/session/utils/summaryMenu');
  const anchors = summaryAnchorIds(summary);
  const finalBlockId = String(summary?.finalBlockId || '').trim();
  let text = '';
  if (finalBlockId) {
    const { items } = await useWorkspaceStore().getBlockContents([finalBlockId]);
    const block = items[finalBlockId];
    text = String(block?.markdown || block?.preview || '');
  }
  if (!text) text = String(summary?.finalPreview || '');
  void summaryCopyText;
  rowHandlers['assistant-menu'](ev, {
    kind: 'live',
    key: `live-${turnId}`,
    msg: { id: anchors.assistantId, content: text },
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
/* 过渡动画核心约束：高度不做任何动画；leave 必须 absolute 脱流，
   否则段毕业时会「下移（leaving 仍占位）→ 上移（移除）」两次抖动，比不加动画更糟。 */
.live-turn-enter-active {
  transition:
    opacity 0.16s ease,
    transform 0.16s ease;
}
.live-turn-enter-from {
  opacity: 0;
  transform: translateY(6px);
}
.live-turn-leave-active {
  transition: opacity 0.14s ease;
}
.live-turn-leave-to {
  opacity: 0;
}
.seg-list {
  /* TransitionGroup 的 leave 元素 absolute 化后需要一个定位容器 */
  position: relative;
}
.seg-enter-active {
  transition:
    opacity 0.14s ease,
    transform 0.14s ease;
}
.seg-enter-from {
  opacity: 0;
  transform: translateY(-4px);
}
.seg-leave-active {
  position: absolute;
  left: 0;
  right: 0;
  transition: opacity 0.12s ease;
  pointer-events: none;
}
.seg-leave-to {
  opacity: 0;
}
@media (prefers-reduced-motion: reduce) {
  .live-turn-enter-active,
  .live-turn-leave-active,
  .seg-enter-active,
  .seg-leave-active {
    transition: none;
  }
}
</style>
