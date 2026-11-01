<!-- src/Web.Client/src/components/sidebar/SessionItem.vue -->
<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import type { SessionListItem } from '@/stores/sessions';
import { useSessionsStore } from '@/stores/sessions';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { copyText } from '@/utils/clipboard';
import { formatRelativeTime } from '@/utils/format';
import HInput from '@/components/base/HInput.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import SessionContextMenu, { type SessionMenuKey } from './SessionContextMenu.vue';

/**
 * 会话列表项：固定 56px 高（虚拟滚动要求），标题 1 行省略，
 * 副标题相对时间，⋯ 悬停出现操作菜单，选中 2px accent 竖条。
 * 删除为原地两段确认：点垃圾桶 → 图标变问号待命，再点一次执行。
 */
const props = defineProps<{ session: SessionListItem; height: number }>();

const sessions = useSessionsStore();
const toast = useToast();

const editing = ref(false);
const draft = ref('');
const menuOpen = ref(false);
const armingDelete = ref(false);
const editInput = ref<InstanceType<typeof HInput> | null>(null);

let armTimer: ReturnType<typeof setTimeout> | null = null;

const active = computed(() => sessions.currentId === props.session.id);
const title = computed(() => props.session.title || '新对话');
const sub = computed(() => formatRelativeTime(props.session.mtime));
const deleteLabel = computed(() => (armingDelete.value ? '再次点击确认删除' : '删除会话'));

function cancelArming(): void {
  armingDelete.value = false;
  if (armTimer !== null) {
    clearTimeout(armTimer);
    armTimer = null;
  }
}

function onDeleteClick(): void {
  if (editing.value) return;
  if (armingDelete.value) {
    void doDelete();
    return;
  }
  armingDelete.value = true;
  if (armTimer !== null) clearTimeout(armTimer);
  armTimer = setTimeout(cancelArming, 3000);
}

// 虚拟滚动会复用行组件：会话切换 / 卸载时必须解除待命，防止误删下一行。
watch(() => props.session.id, cancelArming);
onBeforeUnmount(cancelArming);

function focusEdit(): void {
  const root = editInput.value?.$el;
  const input = root instanceof HTMLElement ? root.querySelector('input') : null;
  if (input instanceof HTMLElement) input.focus();
}

function startRename(): void {
  draft.value = props.session.title;
  editing.value = true;
  menuOpen.value = false;
  cancelArming();
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
  cancelArming();
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

    <div v-if="!editing" class="sess-actions" @mouseleave="cancelArming">
      <button
        class="sess-del"
        :class="{ arming: armingDelete }"
        type="button"
        :aria-label="deleteLabel"
        :title="deleteLabel"
        @click.stop="onDeleteClick"
        @keydown.esc.prevent="cancelArming"
      >
        <HaloIcon :name="armingDelete ? 'help' : 'trash'" :size="14" />
      </button>
      <SessionContextMenu :open="menuOpen" @update:open="menuOpen = $event" @select="onMenuSelect">
        <template #trigger>
           <button
             class="sess-more"
             type="button"
             aria-label="会话操作"
             aria-haspopup="menu"
             :aria-expanded="menuOpen ? 'true' : 'false'"
           >
            <HaloIcon name="more-horizontal" :size="14" />
          </button>
        </template>
      </SessionContextMenu>
    </div>
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
.sess-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 2px;
}
.sess-more,
.sess-del {
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
.sess-item:hover .sess-del,
.sess-more:focus-visible,
.sess-del:focus-visible,
.sess-del.arming {
  opacity: 1;
}
.sess-more:hover,
.sess-del:hover {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg));
}
 .sess-del.arming {
   color: hsl(var(--danger));
   background: hsl(var(--bg-muted));
 }
</style>
