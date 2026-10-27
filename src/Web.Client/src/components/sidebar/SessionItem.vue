<!-- src/Web.Client/src/components/sidebar/SessionItem.vue -->
<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import type { SessionListItem } from '@/stores/sessions';
import { useSessionsStore } from '@/stores/sessions';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { copyText } from '@/utils/clipboard';
import { formatRelativeTime } from '@/utils/format';
import HInput from '@/components/base/HInput.vue';
import HDialog from '@/components/base/HDialog.vue';
import HButton from '@/components/base/HButton.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import SessionContextMenu, { type SessionMenuKey } from './SessionContextMenu.vue';

/**
 * 会话列表项：固定 56px 高（虚拟滚动要求），标题 1 行省略，
 * 副标题相对时间，⋯ 悬停出现操作菜单，选中 2px accent 竖条。
 */
const props = defineProps<{ session: SessionListItem; height: number }>();

const sessions = useSessionsStore();
const toast = useToast();

const editing = ref(false);
const draft = ref('');
const menuOpen = ref(false);
const confirmDelete = ref(false);
const editInput = ref<InstanceType<typeof HInput> | null>(null);

const active = computed(() => sessions.currentId === props.session.id);
const title = computed(() => props.session.title || '新对话');
const sub = computed(() => formatRelativeTime(props.session.mtime));

function focusEdit(): void {
  const root = editInput.value?.$el;
  const input = root instanceof HTMLElement ? root.querySelector('input') : null;
  if (input instanceof HTMLElement) input.focus();
}

function startRename(): void {
  draft.value = props.session.title;
  editing.value = true;
  menuOpen.value = false;
  void nextTick(focusEdit);
}

async function commitRename(): Promise<void> {
  if (!editing.value) return;
  editing.value = false;
  const next = draft.value.trim();
  if (!next) {
    toast.warning('名称不能为空');
    return;
  }
  if (next === props.session.title) return;
  if (next.length > 80) {
    toast.warning('名称不能超过 80 个字符');
    return;
  }
  try {
    await transport.request('session.rename', { id: props.session.id, note: next });
    await sessions.load();
  } catch (e) {
    toast.error(`重命名失败：${(e as Error).message}`);
  }
}

function cancelRename(): void {
  editing.value = false;
  draft.value = '';
}

async function open(): Promise<void> {
  if (active.value || editing.value) return;
  try {
    await sessions.select(props.session.id);
  } catch (e) {
    toast.error(`打开会话失败：${(e as Error).message}`);
  }
}

async function doDelete(): Promise<void> {
  confirmDelete.value = false;
  const wasActive = active.value;
  try {
    await transport.request('session.delete', { id: props.session.id });
    await sessions.load();
    toast.success('已删除');
    if (wasActive) await sessions.createNew();
  } catch (e) {
    toast.error(`删除失败：${(e as Error).message}`);
  }
}

async function onMenuSelect(key: SessionMenuKey): Promise<void> {
  menuOpen.value = false;
  if (key === 'rename') startRename();
  else if (key === 'delete') confirmDelete.value = true;
  else if (key === 'copy-id') {
    const ok = await copyText(props.session.id);
    toast[ok ? 'success' : 'error'](ok ? '已复制会话 ID' : '复制失败');
  }
}
</script>

<template>
  <div
    class="sess-item"
    :class="{ active }"
    :style="{ height: `${height}px` }"
    role="button"
    tabindex="0"
    :aria-current="active ? 'true' : undefined"
    :aria-label="`打开会话 ${title}`"
    @click="open"
    @keydown.enter.prevent="open"
    @dblclick.prevent="startRename"
  >
    <div class="sess-body">
      <HInput
        v-if="editing"
        ref="editInput"
        v-model="draft"
        size="sm"
        class="sess-edit"
        aria-label="重命名会话"
        @enter="commitRename"
        @keydown.esc.prevent="cancelRename"
        @blur="commitRename"
        @click.stop
      />
      <template v-else>
        <div class="sess-title" :title="title">{{ title }}</div>
        <div class="sess-sub">{{ sub }}</div>
      </template>
    </div>

    <SessionContextMenu :open="menuOpen" @update:open="menuOpen = $event" @select="onMenuSelect">
      <template #trigger>
        <button
          class="sess-more"
          type="button"
          aria-label="会话操作"
          @click.stop="menuOpen = !menuOpen"
        >
          <HaloIcon name="more-horizontal" :size="14" />
        </button>
      </template>
    </SessionContextMenu>

    <HDialog :open="confirmDelete" title="删除会话？" @update:open="confirmDelete = $event">
      <p class="del-line">{{ title }}</p>
      <p class="del-desc">删除后会连同其历史文件一起移除，无法恢复。</p>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="confirmDelete = false">取消</HButton>
        <HButton variant="danger" size="sm" @click="doDelete">删除</HButton>
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.sess-item {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  padding: 0 var(--space-2) 0 var(--space-3);
  border-left: 2px solid transparent;
  cursor: pointer;
  min-width: 0;
  box-sizing: border-box;
}
.sess-item:hover {
  background: hsl(var(--bg-subtle));
}
.sess-item.active {
  background: hsl(var(--bg-muted));
  border-left-color: hsl(var(--accent));
}
.sess-item:focus-visible {
  box-shadow: var(--shadow-focus);
}
.sess-body {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.sess-title {
  font-size: var(--text-sm);
  color: hsl(var(--fg));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.sess-sub {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.sess-edit {
  width: 100%;
}
.sess-more {
  opacity: 0;
  flex: none;
  background: transparent;
  border: 0;
  padding: 3px;
  cursor: pointer;
  color: hsl(var(--fg-subtle));
  border-radius: var(--radius-xs);
}
.sess-item:hover .sess-more,
.sess-more:focus-visible {
  opacity: 1;
}
.del-line {
  margin: 0 0 var(--space-2);
  font-weight: 500;
}
.del-desc {
  margin: 0;
  color: hsl(var(--fg-muted));
  font-size: var(--text-sm);
}
</style>
