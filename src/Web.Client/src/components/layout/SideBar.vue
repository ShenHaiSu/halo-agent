<!-- src/Web.Client/src/components/layout/SideBar.vue -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HButton from '@/components/base/HButton.vue';
import HInput from '@/components/base/HInput.vue';
import HScroll from '@/components/base/HScroll.vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HSkeleton from '@/components/base/HSkeleton.vue';
import HMenu from '@/components/base/HMenu.vue';
import HMenuItem from '@/components/base/HMenuItem.vue';
import { useSessionsStore } from '@/stores/sessions';
import { useUiStore } from '@/stores/ui';
import { formatRelativeTime } from '@/utils/format';

/**
 * 侧栏：会话列表。
 *
 * ============ 结构 ============
 *  顶部：新建会话按钮 + 搜索
 *  中部：会话列表（可滚动，时间分组）
 *  底部：设置入口
 *
 *  ============ P4 只做骨架 ============
 *  列表项的点击、右键菜单的实际行为在 P5 实现。
 *  这里保证：布局、滚动、空状态、加载态、分组标题都正确。
 * ============ ============
 */
const sessions = useSessionsStore();
const ui = useUiStore();

const query = ref('');

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase();
  if (!q) return sessions.list;
  return sessions.list.filter((s) => s.title.toLowerCase().includes(q));
});

/** 按时间分组：今天 / 昨天 / 近 7 天 / 更早 */
const groups = computed(() => {
  const now = new Date();
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const startOfYesterday = startOfToday - 86400_000;
  const startOf7Days = startOfToday - 6 * 86400_000;

  const buckets: Record<string, typeof filtered.value> = {
    today: [],
    yesterday: [],
    week: [],
    older: [],
  };

  for (const s of filtered.value) {
    if (s.mtime >= startOfToday) buckets.today!.push(s);
    else if (s.mtime >= startOfYesterday) buckets.yesterday!.push(s);
    else if (s.mtime >= startOf7Days) buckets.week!.push(s);
    else buckets.older!.push(s);
  }

  return [
    { key: 'today', label: '今天', items: buckets.today! },
    { key: 'yesterday', label: '昨天', items: buckets.yesterday! },
    { key: 'week', label: '近 7 天', items: buckets.week! },
    { key: 'older', label: '更早', items: buckets.older! },
  ].filter((g) => g.items.length > 0);
});

async function onNewSession() {
  await sessions.createNew();
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- 顶部 -->
    <div class="shrink-0 space-y-2 border-b border-border-subtle p-3">
      <HButton variant="primary" size="md" icon="message-plus" block @click="onNewSession">
        新建会话
      </HButton>

      <HInput v-model="query" size="sm" type="search" placeholder="搜索会话…" clearable>
        <template #prefix>
          <HaloIcon name="search" :size="13" />
        </template>
      </HInput>
    </div>

    <!-- 列表 -->
    <HScroll class="min-h-0 flex-1" auto-follow>
      <div class="p-2">
        <!-- 加载态 -->
        <div v-if="sessions.loading" class="space-y-2 px-1 py-2">
          <HSkeleton :lines="3" />
          <HSkeleton :lines="2" />
        </div>

        <!-- 空态 -->
        <HEmpty
          v-else-if="filtered.length === 0"
          icon="message"
          :title="query ? '没有匹配的会话' : '还没有会话'"
          :description="query ? '试试换个关键词。' : '点上面的「新建会话」开始第一次对话。'"
          size="sm"
        />

        <!-- 分组列表 -->
        <template v-else>
          <div v-for="g in groups" :key="g.key" class="mb-2">
            <div class="px-2 py-1 text-2xs font-medium tracking-wide text-fg-subtle">
              {{ g.label }}
            </div>
            <button
              v-for="s in g.items"
              :key="s.id"
              type="button"
              class="group flex w-full items-start gap-2 rounded-md px-2 py-1.5 text-left transition-colors duration-fast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
              :class="
                s.id === sessions.currentId
                  ? 'bg-accent-subtle text-accent'
                  : 'text-fg-muted hover:bg-bg-muted hover:text-fg'
              "
              @click="sessions.select(s.id)"
            >
              <HaloIcon
                :name="s.headless ? 'terminal' : 'message'"
                :size="13"
                class="mt-0.5 shrink-0 opacity-70"
              />
              <span class="min-w-0 flex-1">
                <span class="block u-truncate text-sm">{{ s.title }}</span>
                <span class="block text-2xs text-fg-subtle tabular-nums">
                  {{ formatRelativeTime(s.mtime) }}
                </span>
              </span>

              <!-- 行内操作（hover 显示） -->
              <span class="shrink-0 opacity-0 transition-opacity group-hover:opacity-100">
                <HMenu align="end" :min-width="140">
                  <template #trigger>
                    <span
                      class="flex h-5 w-5 items-center justify-center rounded hover:bg-bg-subtle focus-visible:ring-2 focus-visible:ring-accent/40"
                      role="button"
                      tabindex="0"
                      aria-label="会话操作"
                      @click.stop
                    >
                      <HaloIcon name="more-horizontal" :size="14" />
                    </span>
                  </template>
                  <HMenuItem icon="edit" @click="sessions.rename(s.id)">重命名</HMenuItem>
                  <HMenuItem icon="copy" @click="sessions.duplicate(s.id)">复制</HMenuItem>
                  <HMenuItem icon="trash" danger @click="sessions.remove(s.id)">删除</HMenuItem>
                </HMenu>
              </span>
            </button>
          </div>
        </template>
      </div>
    </HScroll>

    <!-- 底部 -->
    <div class="shrink-0 border-t border-border-subtle p-2">
      <button
        type="button"
        class="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-sm text-fg-muted transition-colors duration-fast hover:bg-bg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
        @click="ui.openSettings()"
      >
        <HaloIcon name="settings" :size="14" />
        设置
      </button>
    </div>
  </div>
</template>
