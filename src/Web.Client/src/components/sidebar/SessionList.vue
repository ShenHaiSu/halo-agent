<!-- src/Web.Client/src/components/sidebar/SessionList.vue -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useSessionsStore, type SessionListItem } from '@/stores/sessions';
import { useToast } from '@/composables/useToast';
import SessionItem from './SessionItem.vue';
import SessionGroupHeader from './SessionGroupHeader.vue';
import SessionSearch from './SessionSearch.vue';
import NewSessionButton from './NewSessionButton.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HButton from '@/components/base/HButton.vue';
import HSkeleton from '@/components/base/HSkeleton.vue';

/**
 * 会话列表（见 04 §5.3 最终版）。
 *
 * ============ 扁平化 + 固定行高的虚拟滚动 ============
 *  为什么扁平化：分组头与 item 混在同一个数组里，滚动计算才简单。
 *  为什么固定行高：
 *    ① 不需要测量（避免 getBoundingClientRect 引起的强制同步布局）
 *    ② 500 条时计算 O(1)，无抖动
 *  行高：
 *    item = 56px，group header = 28px
 *  buffer：上下各 5 个（快速滚动时不白屏）
 * ===================================================
 */
const sessions = useSessionsStore();
const toast = useToast();

const ITEM_H = 56;
const HEAD_H = 28;
const BUFFER = 5;

type GroupKey = 'today' | 'yesterday' | 'week' | 'earlier';
const GROUP_LABEL: Record<GroupKey, string> = {
  today: '今天',
  yesterday: '昨天',
  week: '最近 7 天',
  earlier: '更早',
};

const query = ref('');

/** 本地过滤，不调 session.list（读磁盘，500 条输入会卡） */
const filtered = computed(() => {
  const q = query.value.trim().toLowerCase();
  if (!q) return sessions.list;
  return sessions.list.filter((s) => s.title.toLowerCase().includes(q));
});

interface Group {
  key: GroupKey;
  label: string;
  items: SessionListItem[];
}

/** 分组（组内按 mtime 降序） */
const grouped = computed<Group[]>(() => {
  const now = new Date();
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const day = 86_400_000;

  const buckets: Record<GroupKey, SessionListItem[]> = {
    today: [],
    yesterday: [],
    week: [],
    earlier: [],
  };

  for (const s of filtered.value) {
    const d = s.mtime;
    if (d >= startOfToday) buckets.today.push(s);
    else if (d >= startOfToday - day) buckets.yesterday.push(s);
    else if (d >= startOfToday - 7 * day) buckets.week.push(s);
    else buckets.earlier.push(s);
  }

  for (const k of Object.keys(buckets) as GroupKey[]) {
    buckets[k].sort((a, b) => b.mtime - a.mtime);
  }

  return (['today', 'yesterday', 'week', 'earlier'] as GroupKey[])
    .filter((k) => buckets[k].length > 0)
    .map((k) => ({ key: k, label: GROUP_LABEL[k], items: buckets[k] }));
});

type Row =
  | { kind: 'header'; key: string; label: string; h: number }
  | { kind: 'item'; key: string; session: SessionListItem; h: number };

const rows = computed<Row[]>(() => {
  const out: Row[] = [];
  for (const g of grouped.value) {
    out.push({ kind: 'header', key: `h-${g.key}`, label: g.label, h: HEAD_H });
    for (const s of g.items) {
      out.push({ kind: 'item', key: s.id, session: s, h: ITEM_H });
    }
  }
  return out;
});

/** 精确累加偏移（混合行高时取整会累积偏差） */
function buildOffsets(hs: number[]): number[] {
  const out = new Array<number>(hs.length + 1);
  out[0] = 0;
  for (let i = 0; i < hs.length; i++) out[i + 1] = out[i]! + hs[i]!;
  return out;
}

/** 二分查找 y 所在行 */
function findStartRow(offsets: number[], y: number): number {
  const n = offsets.length - 1;
  if (n <= 0) return 0;
  if (y <= 0) return 0;
  if (y >= offsets[n]!) return n - 1;
  let lo = 0;
  let hi = n - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (offsets[mid + 1]! <= y) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

const offsets = computed(() => buildOffsets(rows.value.map((r) => r.h)));
const totalHeight = computed(() => offsets.value[offsets.value.length - 1] ?? 0);

const viewport = ref<HTMLElement | null>(null);
const scrollTop = ref(0);
const viewportH = ref(600);

function onScroll(): void {
  const el = viewport.value;
  if (!el) return;
  scrollTop.value = el.scrollTop;
}

let ro: ResizeObserver | null = null;
onMounted(() => {
  if (!sessions.list.length && !sessions.loading) {
    void sessions.load().catch((e: unknown) => {
      toast.error(`加载会话列表失败：${(e as Error).message}`);
    });
  }
  const el = viewport.value;
  if (el) viewportH.value = el.clientHeight;
  if (el && typeof ResizeObserver !== 'undefined') {
    ro = new ResizeObserver(() => {
      if (el) viewportH.value = el.clientHeight;
    });
    ro.observe(el);
  }
});
onBeforeUnmount(() => ro?.disconnect());

const visible = computed(() => {
  const off = offsets.value;
  const n = off.length - 1;
  if (n <= 0) return { start: 0, end: 0, padTop: 0 };
  const first = findStartRow(off, scrollTop.value);
  const start = Math.max(0, first - BUFFER);
  const bottomY = scrollTop.value + viewportH.value;
  let last = first;
  while (last < n && off[last]! < bottomY) last++;
  const end = Math.min(n, last + BUFFER);
  return { start, end, padTop: off[start] ?? 0 };
});

const slice = computed(() => rows.value.slice(visible.value.start, visible.value.end));

/**
 * 过滤时保持滚动位置：
 *  开始搜索 → 保持当前位置（用户能看到过滤结果）
 *  清空搜索 → 回到顶部（否则看到的是列表中间）
 */
watch(query, (now, before) => {
  const el = viewport.value;
  if (!el) return;
  if (now && !before) return;
  if (!now && before) {
    el.scrollTop = 0;
    scrollTop.value = 0;
  }
});

async function createNew(): Promise<void> {
  try {
    await sessions.createNew();
  } catch (e) {
    toast.error(`新建会话失败：${(e as Error).message}`);
  }
}
</script>

<template>
  <div class="sess-pane" data-sidebar>
    <SessionSearch v-model="query" />

    <div
      ref="viewport"
      class="sess-viewport"
      role="list"
      aria-label="会话列表"
      @scroll.passive="onScroll"
    >
      <div v-if="sessions.loading" class="sess-loading">
        <HSkeleton shape="block" height="56px" />
        <HSkeleton shape="block" height="56px" />
        <HSkeleton shape="block" height="56px" />
      </div>

      <template v-else-if="rows.length">
        <div class="sess-spacer" :style="{ height: `${totalHeight}px` }">
          <div class="sess-window" :style="{ transform: `translateY(${visible.padTop}px)` }">
            <template v-for="r in slice" :key="r.key">
              <SessionGroupHeader v-if="r.kind === 'header'" :label="r.label" :height="r.h" />
              <SessionItem v-else :session="r.session" :height="r.h" />
            </template>
          </div>
        </div>
      </template>

      <HEmpty
        v-else
        icon="message"
        :title="query ? `没有匹配「${query}」的会话` : '还没有对话'"
        :description="query ? '试试其他关键词' : '点击下方按钮开始第一次对话'"
        size="sm"
      >
        <template #action>
          <HButton v-if="query" variant="ghost" size="sm" @click="query = ''">清除搜索</HButton>
          <HButton v-else variant="primary" size="sm" @click="createNew">新建对话</HButton>
        </template>
      </HEmpty>
    </div>

    <NewSessionButton />
  </div>
</template>

<style scoped>
.sess-pane {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  min-width: 0;
}
.sess-viewport {
  flex: 1;
  min-height: 0; /* * */
  overflow-y: auto;
  overflow-x: hidden;
  overscroll-behavior: contain;
}
.sess-loading {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  padding: var(--space-2);
}
.sess-spacer {
  position: relative;
}
.sess-window {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  will-change: transform;
}
</style>
