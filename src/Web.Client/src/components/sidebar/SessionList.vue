<!-- src/Web.Client/src/components/sidebar/SessionList.vue -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useSessionsStore, type SessionListItem } from '@/stores/sessions';
import { useBootStore } from '@/stores/boot';
import { useToast } from '@/composables/useToast';
import SessionItem from './SessionItem.vue';
import SessionGroupHeader from './SessionGroupHeader.vue';
import SessionSearch from './SessionSearch.vue';
import NewSessionButton from './NewSessionButton.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HButton from '@/components/base/HButton.vue';
import HSkeleton from '@/components/base/HSkeleton.vue';
import { buildOffsets, computeVisible } from './virtualize';

/**
 * 会话列表（见 04 §5.3 最终版）。
 *
 * ============ 扁平化 + 固定行高的虚拟滚动 ============
 *  为什么扁平化：分组头与 item 混在同一个数组里，滚动计算才简单。
 *  为什么固定行高：
 *    ① 不需要测量（避免 getBoundingClientRect 引起的强制同步布局）
 *    ② 500 条时计算 O(1)，无抖动
 *  行高：
 *    item = 56px，group header = 22px（须与 .group-head 实际渲染高度逐像素一致）
 *  buffer：上下各 5 个（快速滚动时不白屏）
 *  注：分组标题是静态分隔行，不能使用 sticky —— transform 祖先会劫持吸附参照系，
 *      padTop 与 scrollTop 不等时补偿量会暴露成可见错位。
 * ===================================================
 */
const sessions = useSessionsStore();
const toast = useToast();

const ITEM_H = 56;
const HEAD_H = 22;
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

/** 偏移累加复用 virtualize.ts（Row 结构兼容 RowLike，直接传对象数组） */
const offsets = computed(() => buildOffsets(rows.value));
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
  // 启动门：boot 阶段由 App.vue 在引擎就绪后统一加载，这里不抢跑。
  if (!useBootStore().isBooting && !sessions.list.length && !sessions.loading) {
    void sessions.load().catch((e: unknown) => {
      // 极端竞态（请求飞行中门恰好变化）下仍静默，只记日志。
      if (useBootStore().isBooting) {
        console.debug('[boot] 启动期会话列表请求被抑制', e);
        return;
      }
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

const visible = computed(() =>
  computeVisible(offsets.value, scrollTop.value, viewportH.value, BUFFER)
);

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
    // store 内已对 state.busy toast，调用方不再重复。
    if (String((e as Error)?.message ?? '').includes('有回合正在执行')) return;
    toast.error(`新建会话失败：${(e as Error).message}`);
  }
}
</script>

<template>
  <div class="sess-pane" data-sidebar>
    <SessionSearch v-model="query" />

    <NewSessionButton />

    <div
      ref="viewport"
      class="sess-viewport"
      role="group"
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
            <template v-for="(r, i) in slice" :key="r.key">
              <SessionGroupHeader
                v-if="r.kind === 'header'"
                :label="r.label"
                :height="r.h"
                :first="visible.start + i === 0"
              />
              <SessionItem v-else :session="r.session" :height="r.h" />
            </template>
          </div>
        </div>
      </template>

      <HEmpty
        v-else
        icon="message"
        :title="query ? `没有匹配「${query}」的会话` : '还没有对话'"
        :description="query ? '试试其他关键词' : '点击上方按钮开始第一次对话'"
        size="sm"
      >
        <template #action>
          <HButton v-if="query" variant="ghost" size="sm" @click="query = ''">清除搜索</HButton>
          <HButton v-else variant="primary" size="sm" @click="createNew">新建对话</HButton>
        </template>
      </HEmpty>
    </div>
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
