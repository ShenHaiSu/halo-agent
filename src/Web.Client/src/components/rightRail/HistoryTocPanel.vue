<template>
  <div class="toc-root" data-testid="history-toc-panel">
    <div class="toc-head">
      <span class="toc-title">{{ isSub ? '子会话目录（只读）' : '历史目录' }}</span>
      <span class="toc-stat">共 {{ totalCount }} 条</span>
      <n-select
        v-model:value="roleFilter"
        :options="roleOptions"
        size="tiny"
        style="width: 108px"
        data-testid="toc-role-filter"
        aria-label="过滤角色"
      />
      <n-tooltip trigger="hover" placement="top">
        <template #trigger>
          <n-switch v-model:value="roundHeadsOnly" size="small" data-testid="toc-round-heads-switch" aria-label="只看每轮首条" />
        </template>
        只看每轮首条
      </n-tooltip>
      <n-button size="tiny" quaternary title="置顶" data-testid="toc-to-top" @click="toTop">⇡</n-button>
      <n-button size="tiny" quaternary title="置底" data-testid="toc-to-bottom" @click="toBottom">⇣</n-button>
    </div>
    <div v-if="hintBar" class="toc-hint" data-testid="toc-hint-bar">
      点击气泡跳转到对应消息；点击外部不会关闭本栏，请点右上角 ✕ 关闭。
      <n-button size="tiny" quaternary @click="dismissHint">知道了</n-button>
    </div>
    <div v-if="loading" class="toc-list" aria-busy="true">
      <div v-for="i in 5" :key="i" class="toc-skeleton" />
    </div>
    <div v-else-if="totalCount === 0" class="toc-empty" data-testid="toc-empty">
      <Bot class="w-8 h-8 text-accent" />
      <p class="toc-empty-title">当前会话还没有消息</p>
      <p class="toc-empty-desc">在下方输入指令后，这里会出现可跳转的历史目录</p>
    </div>
    <div v-else ref="listRef" class="toc-list" data-testid="toc-list" @scroll.passive="onListScroll">
      <div
        v-for="item in visibleItems"
        :key="item.anchorKey"
        class="toc-row"
        :class="[`toc-row--${item.role}`]"
      >
        <HistoryBubble :item="item" :active="item.anchorKey === activeKey" @jump="onJump" />
      </div>
      <div v-if="hasMoreBefore" class="toc-more">
        <n-button size="tiny" @click="showEarlier" data-testid="toc-show-earlier">加载更早 {{ Math.min(PAGE, remainingBefore) }} 条</n-button>
      </div>
      <div v-if="streamingPulse" class="toc-row toc-row--assistant">
        <span class="toc-bubble toc-bubble--assistant toc-pulse" aria-disabled="true">正在生成…</span>
      </div>
      <div v-if="hasMoreAfter" class="toc-more">
        <n-button size="tiny" @click="showLater" data-testid="toc-show-later">加载更晚 {{ Math.min(PAGE, remainingAfter) }} 条</n-button>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
/**
 * need19：历史气泡目录面板。
 * 数据唯一来源：workspaceStore.sessionMessages（主）/ subSessionStore.messages（子）。
 * 通过 useTimelineNav().scrollToAnchor() 跳转，不直接操作 DOM。
 */
import { computed, nextTick, ref, watch } from 'vue';
import { Bot } from 'lucide-vue-next';
import { useMessage } from 'naive-ui';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSubSessionStore } from '@/stores/subSessionStore';
import { useAgentStore } from '@/stores/agentStore';
import { useUiStore } from '@/stores/uiStore';
import { useTimelineNav } from '@/composables/useTimelineNav';
import { buildTurnGroups, buildTimelineEntries, type TimelineEntry } from '@/utils/turnGroup';
import { buildTimelineItems } from '@/composables/useTimelineGroups';
import { buildHistoryToc, type TocItem } from '@/utils/historyToc';
import HistoryBubble from './HistoryBubble.vue';

const PAGE = 200;

const ws = useWorkspaceStore();
const sub = useSubSessionStore();
const agent = useAgentStore();
const ui = useUiStore();
const message = useMessage();
const nav = useTimelineNav();

const { sessionMessages, loadingMessages, activeSessionId } = storeToRefs(ws);

const isSub = computed(() => sub.mode === 'sub');
const loading = computed(() => (isSub.value ? sub.loading : loadingMessages.value));
const streamingPulse = computed(() => !isSub.value && agent.hasActiveTurn);

const roleOptions = [
  { label: '全部', value: 'all' },
  { label: '只看提问', value: 'user' },
  { label: '只看回复', value: 'assistant' },
];
const roleFilter = computed({
  get: () => ui.tocRole,
  set: (v: 'all' | 'user' | 'assistant') => { ui.tocRole = v; resetWindow(); },
});
const roundHeadsOnly = computed({
  get: () => ui.tocRoundHeadsOnly,
  set: (v: boolean) => { ui.tocRoundHeadsOnly = v; resetWindow(); },
});

const entries = computed<TimelineEntry[]>(() => {
  if (isSub.value) {
    const simple = buildTimelineItems(sub.messages || []);
    return simple.map((s) => ({ key: s.key, kind: s.kind, msg: s.msg, group: s.group as unknown as TimelineEntry['group'] }));
  }
  const groups = buildTurnGroups(sessionMessages.value || []);
  const hidden = agent.hiddenToolCallIds as unknown as Set<string> | string[];
  const hiddenSet = hidden instanceof Set ? hidden : new Set([...(hidden as string[])]);
  let visible = groups;
  if (hiddenSet.size > 0) {
    visible = [];
    for (const g of groups) {
      const kept = g.toolMessages.filter((m) => !hiddenSet.has(String((m as unknown as { toolCallId?: string }).toolCallId || '')));
      if (!g.content && !g.reasoning && kept.length === 0) continue;
      visible.push({ ...g, toolMessages: kept });
    }
  }
  return buildTimelineEntries(sessionMessages.value || [], visible);
});

const tocItems = computed<TocItem[]>(() =>
  buildHistoryToc(entries.value, {
    role: ui.tocRole,
    roundHeadsOnly: ui.tocRoundHeadsOnly,
    skipSystem: ui.tocSkipSystem,
  })
);
const totalCount = computed(() => tocItems.value.length);

const activeKey = ref<string | null>(null);
const listRef = ref<HTMLElement | null>(null);
// L1 分批：首屏最新一屏，滚动加载更早/更晚
const windowEnd = ref<number>(0); // 切片右边界（exclusive）
const windowSize = ref(PAGE);
function resetWindow(): void {
  windowEnd.value = 0;
  windowSize.value = PAGE;
}
watch([tocItems, activeSessionId], () => { resetWindow(); activeKey.value = null; }, { immediate: true });
const visibleItems = computed(() => {
  const all = tocItems.value;
  if (all.length <= 400) return all;
  const end = windowEnd.value > 0 ? windowEnd.value : all.length;
  const start = Math.max(0, end - windowSize.value);
  return all.slice(start, end);
});
const hasMoreBefore = computed(() => {
  const all = tocItems.value;
  if (all.length <= 400) return false;
  const end = windowEnd.value > 0 ? windowEnd.value : all.length;
  return end - windowSize.value > 0;
});
const hasMoreAfter = computed(() => {
  const all = tocItems.value;
  if (all.length <= 400) return false;
  const end = windowEnd.value > 0 ? windowEnd.value : all.length;
  return end < all.length;
});
const remainingBefore = computed(() => {
  const all = tocItems.value;
  const end = windowEnd.value > 0 ? windowEnd.value : all.length;
  return Math.max(0, end - windowSize.value);
});
const remainingAfter = computed(() => {
  const all = tocItems.value;
  const end = windowEnd.value > 0 ? windowEnd.value : all.length;
  return Math.max(0, all.length - end);
});
function showEarlier(): void { windowSize.value += PAGE; }
function showLater(): void {
  const all = tocItems.value;
  const end = windowEnd.value > 0 ? windowEnd.value : all.length;
  windowEnd.value = Math.min(all.length, end + PAGE);
  if (windowEnd.value >= all.length) windowEnd.value = 0;
  if (windowEnd.value === 0) windowSize.value = all.length;
}
function onListScroll(): void { /* L1 不做无限滚动，仅按钮分页，避免误触 */ }

const hintBar = computed(() => !ui.hintShown);
function dismissHint(): void { ui.markHintShown(); }

async function onJump(item: TocItem): Promise<void> {
  const ok = nav.scrollToAnchor(item.anchorKey, { behavior: 'smooth', highlight: true });
  if (!ok) {
    message.warning('未找到该消息，可能正在加载或已被压缩');
    return;
  }
  ui.lockJump(1500);
  activeKey.value = item.anchorKey;
}

function toTop(): void {
  const first = tocItems.value[0];
  if (!first) return;
  const ok = nav.scrollToAnchor(first.anchorKey, { behavior: 'smooth', highlight: true });
  if (ok) { ui.lockJump(1500); activeKey.value = first.anchorKey; }
}
function toBottom(): void {
  nav.scrollToBottom();
  ui.lockJump(1500);
  const last = tocItems.value[tocItems.value.length - 1];
  if (last) {
    activeKey.value = last.anchorKey;
    void nextTick(() => { nav.scrollToAnchor(last.anchorKey, { behavior: 'auto', highlight: true }); });
  }
}

watch(activeSessionId, () => { activeKey.value = null; });
</script>
<style scoped>
.toc-root { display: flex; flex-direction: column; min-height: 0; height: 100%; }
.toc-head { display: flex; align-items: center; gap: 6px; height: 34px; flex: 0 0 auto; }
.toc-title { font-size: 13px; font-weight: 600; color: var(--text-primary); }
.toc-stat { font-size: 11px; color: var(--text-secondary); font-family: var(--font-mono); }
.toc-hint { font-size: 11px; color: var(--text-secondary); background: var(--bg-elevated); border: 1px solid var(--border-subtle); border-radius: 8px; padding: 6px 8px; margin: 4px 0; display: flex; align-items: center; gap: 8px; }
.toc-list { flex: 1 1 auto; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 6px; padding: 4px 2px 12px; }
.toc-row { display: flex; }
.toc-row--user { justify-content: flex-end; }
.toc-row--assistant { justify-content: flex-start; }
.toc-row--system { justify-content: center; }
.toc-skeleton { height: 28px; border-radius: 10px; background: var(--bg-elevated); border: 1px solid var(--border-subtle); opacity: .7; }
.toc-empty { flex: 1 1 auto; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 6px; color: var(--text-secondary); }
.toc-empty-title { font-size: 13px; font-weight: 600; color: var(--text-primary); margin: 0; }
.toc-empty-desc { font-size: 12px; margin: 0; }
.toc-more { display: flex; justify-content: center; padding: 2px 0; }
.toc-pulse { animation: toc-pulse 1.2s ease-in-out infinite; }
@keyframes toc-pulse { 0%,100% { opacity: .55; } 50% { opacity: 1; } }
</style>
