<template>
  <div
    class="group relative flex items-center justify-between px-2.5 py-2 rounded-md cursor-pointer text-xs transition-colors border"
    :class="
      armed
        ? 'border-red-500/60 bg-red-500/10 text-primary font-medium'
        : active
          ? 'bg-elevated border-accent/40 text-primary font-medium'
          : 'border-transparent text-secondary hover:bg-surface hover:text-primary'
    "
    :data-testid="`session-item-${sess.id}`"
    @click="emit('select', sess.id)"
  >
    <div class="flex items-center gap-2 min-w-0 flex-1">
      <div
        class="w-2 h-2 rounded-full shrink-0"
        :class="running ? 'bg-emerald-400 shadow-sm animate-pulse' : active ? 'bg-accent shadow-sm' : 'bg-secondary/40'"
        :title="running ? '后台运行中' : ''"
      />
      <div class="min-w-0 flex-1">
        <div v-if="editing" class="my-0.5" @click.stop>
          <n-input
            :value="draft"
            size="tiny"
            autofocus
            @update:value="emit('update:draft', $event)"
            @keyup.enter="emit('rename-save', sess.id)"
            @blur="emit('rename-save', sess.id)"
          />
        </div>
        <div v-else class="truncate font-medium" :title="sess.title" @dblclick="emit('rename-start', sess)">
          {{ sess.title }}
        </div>
        <div
          class="flex items-center gap-1.5 text-[11px] text-secondary font-mono mt-0.5 truncate min-w-0 overflow-hidden"
          :title="sessionTimeTitle(sess)"
          data-testid="session-meta-line"
          :data-tick="nowTick"
        >
          <template v-if="hasNoTime(sess)"><span>时间未知</span></template>
          <template v-else
            ><span>{{ formatSessionTime(createdOf(sess)) }}</span
            ><span class="opacity-50">·</span><span>活跃 {{ relativeLabel(updatedOf(sess)) }}</span></template
          >
        </div>
        <n-tag
          v-if="askPending"
          type="error"
          size="tiny"
          :bordered="false"
          :data-testid="`sess-ask-badge-${sess.id}`"
          class="mt-1 self-start"
          >待回答</n-tag
        >
      </div>
    </div>
    <div
      class="transition-opacity ml-1.5 shrink-0 flex items-center gap-1"
      :class="armed ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'"
      @click.stop
    >
      <n-button
        quaternary
        circle
        size="tiny"
        data-testid="session-delete-btn"
        :data-armed="armed ? 'true' : 'false'"
        :type="armed ? 'error' : 'default'"
        :loading="deleting"
        :title="armed ? '再次点击确认删除（3s内有效）' : '删除会话'"
        :class="armed ? '!bg-red-500/20 font-semibold' : ''"
        @click="emit('delete', sess.id)"
      >
        <template #icon>
          <Trash2 class="w-3.5 h-3.5" :class="armed ? '' : 'text-error'" />
        </template>
      </n-button>
      <n-dropdown
        trigger="click"
        :options="menuOptions"
        :show="menuOpen"
        @update:show="emit('menu', $event)"
        @select="(key: string | number) => emit('menu-select', String(key))"
      >
        <n-button quaternary circle size="tiny" data-testid="session-more-btn" @click="emit('menu', !menuOpen)">
          <template #icon>
            <MoreVertical class="w-3.5 h-3.5" />
          </template>
        </n-button>
      </n-dropdown>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed, h } from 'vue';
import { Trash2, MoreVertical, Edit3, Pin, Hash, Download } from 'lucide-vue-next';
import type { SessionMetadata } from '@shared/bridge/hostBridge';
import { formatSessionTime, formatRelativeTime, sessionTimeTitle, isBadTime } from '@shared/utils/timeFormat';

const props = defineProps<{
  sess: SessionMetadata;
  active: boolean;
  armed: boolean;
  running: boolean;
  editing: boolean;
  draft: string;
  askPending: boolean;
  deleting: boolean;
  menuOpen: boolean;
  nowTick: number;
}>();
const emit = defineEmits<{
  select: [id: string];
  delete: [id: string];
  menu: [open: boolean];
  'menu-select': [key: string];
  'rename-start': [sess: SessionMetadata];
  'rename-save': [id: string];
  'update:draft': [v: string];
}>();
function createdOf(sess: SessionMetadata): string {
  return (sess as unknown as { createdAtUtc: string }).createdAtUtc;
}
function updatedOf(sess: SessionMetadata): string {
  return (sess as unknown as { updatedAtUtc: string }).updatedAtUtc;
}
function hasNoTime(sess: SessionMetadata): boolean {
  return isBadTime(createdOf(sess)) && isBadTime(updatedOf(sess));
}
function relativeLabel(iso: string): string {
  void props.nowTick;
  return formatRelativeTime(iso);
}
const menuOptions = computed(() => [
  { label: '重命名会话', key: 'rename', icon: () => h(Edit3, { class: 'w-4 h-4' }) },
  { label: props.sess.isPinned ? '取消置顶' : '置顶会话', key: 'pin', icon: () => h(Pin, { class: 'w-4 h-4' }) },
  { label: '复制会话 Hash', key: 'copy-hash', icon: () => h(Hash, { class: 'w-4 h-4' }) },
  { label: '导出为 Markdown', key: 'export-md', icon: () => h(Download, { class: 'w-4 h-4' }) },
]);
</script>
