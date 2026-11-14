<template>
  <div class="timeline-turn min-w-0" :data-turn-key="turn.turnId" :data-testid="`timeline-turn-${turn.turnId}`">
    <div v-if="turn.userPreview" class="flex justify-end turn-user-slot">
      <div
        class="max-w-[85%] rounded-lg bg-elevated border border-subtle px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
      >
        {{ turn.userPreview }}
      </div>
    </div>
    <div
      class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 overflow-hidden mt-1 turn-process-slot"
      :class="turn.failed > 0 ? 'border-rose-500' : 'border-indigo-500/70'"
      data-testid="timeline-process-box"
    >
      <div
        class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none"
        :data-testid="`timeline-process-head-${turn.turnId}`"
        @click="toggle"
      >
        <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
          <span aria-hidden="true">{{ badge }}</span>
          <span class="truncate">过程内容</span>
          <span v-if="label"> · {{ label }}</span>
        </span>
        <span class="flex items-center gap-1 text-[11px] opacity-80 shrink-0">
          <span>{{ expanded ? '点击折叠' : '点击展开' }}</span>
        </span>
      </div>
    </div>
    <div
      v-if="expanded"
      class="mt-1 rounded-md border border-subtle bg-surface/40 px-3 py-2 min-w-0 timeline-process-body turn-earlier-slot"
      :data-testid="`timeline-process-body-${turn.turnId}`"
    >
      <div v-if="loading" class="text-[11px] text-secondary">过程块加载中…</div>
      <div v-else-if="loadError" class="text-[11px] text-secondary">
        {{ loadError }}
        <button class="underline ml-1" @click.stop="reload">重试</button>
      </div>
      <div v-else-if="orderedBlocks.length === 0" class="text-[11px] text-secondary whitespace-pre-wrap break-words select-text">
        {{ emptyText }}
      </div>
      <div v-else class="flex flex-col gap-1.5">
        <div v-for="g in toolGroups" :key="`tool-${g.callId}`" :data-testid="`timeline-tool-call-${g.callId}`" @click="ensureToolResult(g)">
          <ToolCallCard :card="historyCardFor(g)" :default-collapsed="true" />
        </div>
        <div v-for="b in otherBlocks" :key="b.blockId">
          <div v-if="b.kind === 'tool_result'" class="text-[11px] whitespace-pre-wrap break-words select-text">
            {{ resultPreviewOf(b) }}
          </div>
          <div v-else-if="b.kind === 'reasoning'" class="text-[11px] text-secondary whitespace-pre-wrap break-words opacity-80">
            {{ plainTextOf(b) }}
          </div>
          <div v-else class="text-[11px] whitespace-pre-wrap break-words select-text">
            {{ plainTextOf(b) }}
          </div>
        </div>
        <div v-if="missing.length > 0" class="text-[11px] text-secondary opacity-70">（{{ missing.length }} 个块已删除，仅保留占位）</div>
      </div>
    </div>
    <div
      v-if="!expanded"
      class="timeline-process-placeholder turn-earlier-slot"
      :data-testid="`timeline-process-placeholder-${turn.turnId}`"
      aria-hidden="true"
    />
    <div
      v-if="hasFinalBlock"
      class="mt-1 app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0 turn-final-slot"
      :data-testid="`timeline-final-${turn.turnId}`"
    >
      <div v-if="finalLoading" class="text-[11px] text-secondary" :data-testid="`timeline-final-loading-${turn.turnId}`">
        全文加载中…<span v-if="turn.finalPreview" class="opacity-70">{{ turn.finalPreview }}</span>
      </div>
      <div v-else-if="finalError" class="text-[11px] text-secondary" :data-testid="`timeline-final-error-${turn.turnId}`">
        {{ finalError }}
        <button class="underline ml-1" :data-testid="`timeline-final-retry-${turn.turnId}`" @click.stop="reloadFinal">重试</button>
      </div>
      <div v-else-if="finalMissing" class="text-[11px] text-secondary opacity-70" :data-testid="`timeline-final-missing-${turn.turnId}`">
        正文块已删除，仅保留摘要：<span v-if="turn.finalPreview">{{ turn.finalPreview }}</span
        ><span v-else>（无摘要）</span>
      </div>
      <MarkdownStreamBlocks v-else-if="finalMarkdown" :text="finalMarkdown" :cache-key="finalCacheKey" />
      <div v-else-if="turn.finalPreview" class="whitespace-pre-wrap">{{ turn.finalPreview }}</div>
      <div v-else class="text-[12px] text-secondary">本轮无正文，{{ turn.tools }} 个工具</div>
    </div>
    <div
      v-else-if="turn.finalPreview"
      class="mt-1 app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0 turn-final-slot"
      :data-testid="`timeline-final-${turn.turnId}`"
    >
      {{ turn.finalPreview }}
    </div>
    <div v-else class="mt-1 text-[12px] text-secondary turn-final-slot" :data-testid="`timeline-final-empty-${turn.turnId}`">
      本轮无正文，{{ turn.tools }} 个工具
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import type { FlatTimelineTurn } from '@features/session/utils/timelineSummary';
import { processBoxLabel, statusBadge } from '@features/session/utils/timelineSummary';
import type { TimelineBlockContent } from '@shared/bridge/hostBridge';
import type { AgentToolCard } from '@features/agent/stores/model';
import MarkdownStreamBlocks from '@features/agent/components/MarkdownStreamBlocks.vue';
import ToolCallCard from '@features/agent/components/ToolCallCard.vue';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { markBlockDirty } from '@features/session/utils/markdownDirty';
import { setTurnExpanded } from '../utils/timelineExpansion';
import { pairToolGroupsInOrder, toAgentToolCard, type ToolGroup } from '../utils/historyToolCards';

const props = defineProps<{
  turn: FlatTimelineTurn;
}>();

const emit = defineEmits<{
  (e: 'expand-change', turnId: string, expanded: boolean): void;
}>();

const expanded = ref(false);
const loading = ref(false);
const loadError = ref('');
const blockMap = ref<Record<string, TimelineBlockContent>>({});
const blockOrder = ref<string[]>([]);
const missing = ref<string[]>([]);
const loadedResultText = ref<Record<string, string>>({});
const pendingToolFetch = ref<Record<string, boolean>>({});
let loadedOnce = false;

const ws = useWorkspaceStore();

const badge = computed(() => statusBadge(props.turn.status));
const label = computed(() => processBoxLabel(props.turn));
const emptyText = computed(() => {
  if (props.turn.tools > 0) return `共 ${props.turn.blocks} 个过程块，${props.turn.tools} 个工具调用。`;
  return '暂无过程内容。';
});

const orderedBlocks = computed<TimelineBlockContent[]>(() => {
  const out: TimelineBlockContent[] = [];
  for (const id of blockOrder.value) {
    const b = blockMap.value[id];
    if (b) out.push(b);
  }
  return out;
});

const pairedGroups = computed(() => pairToolGroupsInOrder(orderedBlocks.value));
const toolGroups = computed<ToolGroup[]>(() => pairedGroups.value.tools);
const otherBlocks = computed<TimelineBlockContent[]>(() => pairedGroups.value.others);

function plainTextOf(b: TimelineBlockContent): string {
  const s = String(b.markdown || b.preview || '');
  return s.length > 4000 ? s.slice(0, 4000) + '\n…（已截断，展开工具卡查看全文）' : s;
}

function resultPreviewOf(b: TimelineBlockContent): string {
  if (b.resultRef) return `工具结果较大，已落盘（${b.resultRef}），点击工具卡按需加载全文。`;
  const s = String(b.resultText || b.markdown || b.preview || '');
  return s.length > 2000 ? s.slice(0, 2000) + '\n…（结果过长已截断）' : s || '（空结果）';
}

function historyCardFor(group: ToolGroup): AgentToolCard {
  const base = toAgentToolCard(group, props.turn);
  const override = loadedResultText.value[group.callId];
  if (override !== undefined) {
    return { ...base, resultText: override };
  }
  return base;
}

async function ensureToolResult(group: ToolGroup): Promise<void> {
  const callId = String(group?.callId || '').trim();
  if (!callId) return;
  if (loadedResultText.value[callId] !== undefined) return;
  if (pendingToolFetch.value[callId]) return;
  const resultBlock = group.resultBlock;
  const hasRef = !!String(resultBlock?.resultRef || '').trim();
  const hasText = !!String(resultBlock?.resultText || '');
  if (!hasRef || hasText) return;
  const fetcher = (ws as unknown as { getToolResult?: (turnId: string, cId: string) => Promise<{ resultText?: string | null } | null> })
    .getToolResult;
  if (typeof fetcher !== 'function') return;
  pendingToolFetch.value = { ...pendingToolFetch.value, [callId]: true };
  try {
    const res = await fetcher.call(ws, props.turn.turnId, callId);
    const text = String((res as unknown as { resultText?: string | null })?.resultText ?? '');
    loadedResultText.value = { ...loadedResultText.value, [callId]: text };
  } catch {
    /* keep placeholder, never block card expand */
  } finally {
    const next = { ...pendingToolFetch.value };
    delete next[callId];
    pendingToolFetch.value = next;
  }
}

async function toggle(): Promise<void> {
  expanded.value = !expanded.value;
  try {
    setTurnExpanded(props.turn.turnId, expanded.value);
  } catch {
    /* quota counter never blocks */
  }
  emit('expand-change', props.turn.turnId, expanded.value);
  if (expanded.value && !loadedOnce) {
    await reload();
  }
  if (!expanded.value) {
    for (const id of blockOrder.value) {
      try {
        markBlockDirty(id);
      } catch {
        /* dirty mark never blocks collapse */
      }
    }
    const fid = (props.turn.finalBlockId || '').trim();
    if (fid) {
      try {
        markBlockDirty(fid);
      } catch {
        /* dirty mark never blocks collapse */
      }
    }
  }
}

async function reload(): Promise<void> {
  loading.value = true;
  loadError.value = '';
  try {
    const ids = await ws.getTurnBlockIds(props.turn.turnId);
    if (!ids || ids.length === 0) {
      blockMap.value = {};
      blockOrder.value = [];
      missing.value = [];
      loadedOnce = true;
      return;
    }
    const { items, missing: miss } = await ws.getBlockContents(ids);
    const ordered = ids.filter((id) => items[id]);
    blockMap.value = items;
    blockOrder.value = ordered;
    missing.value = miss || [];
    loadedOnce = true;
  } catch (e) {
    loadError.value = '过程块加载失败。';
  } finally {
    loading.value = false;
  }
}

const hasFinalBlock = computed(() => !!(props.turn.finalBlockId || '').trim());
const finalCacheKey = computed(() => (props.turn.finalBlockId || '').trim());
const finalMarkdown = ref('');
const finalLoading = ref(false);
const finalError = ref('');
const finalMissing = ref(false);

function resetFinal(): void {
  finalMarkdown.value = '';
  finalError.value = '';
  finalMissing.value = false;
}

async function reloadFinal(): Promise<void> {
  const fid = (props.turn.finalBlockId || '').trim();
  if (!fid) {
    resetFinal();
    return;
  }
  finalLoading.value = true;
  finalError.value = '';
  finalMissing.value = false;
  try {
    const { items } = await ws.getBlockContents([fid]);
    const hit = items[fid];
    if (hit) {
      finalMarkdown.value = String(hit.markdown || '');
      finalMissing.value = false;
    } else {
      finalMarkdown.value = '';
      finalMissing.value = true;
    }
  } catch (e) {
    finalError.value = '正文加载失败。';
  } finally {
    finalLoading.value = false;
  }
}

watch(
  () => props.turn.turnId,
  () => {
    loadedOnce = false;
    blockMap.value = {};
    blockOrder.value = [];
    missing.value = [];
    loadedResultText.value = {};
    pendingToolFetch.value = {};
    resetFinal();
    if (expanded.value) void reload();
    else void reloadFinal();
  }
);

watch(
  () => props.turn.finalBlockId,
  () => {
    resetFinal();
    void reloadFinal();
  }
);

onMounted(() => {
  void reloadFinal();
});
</script>

<style scoped>
.timeline-turn {
  display: flex;
  flex-direction: column;
  content-visibility: auto;
  contain-intrinsic-size: auto 240px;
}
.turn-user-slot {
  order: 1;
}
.turn-process-slot {
  order: 2;
}
.turn-earlier-slot {
  order: 3;
}
.turn-final-slot {
  order: 4;
}
.timeline-process-placeholder {
  min-height: 8px;
  content-visibility: auto;
  contain-intrinsic-size: auto 8px;
}
.timeline-process-body {
  content-visibility: auto;
  contain-intrinsic-size: auto 120px;
}
</style>
