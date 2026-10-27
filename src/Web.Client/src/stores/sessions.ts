// src/Web.Client/src/stores/sessions.ts
// D4-8 会话列表 store。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';
import { useMessagesStore } from './messages';
import { useStreamStore } from './stream';
import { formatRelativeTime } from '@/utils/format';

export interface SessionListItem {
  id: string;
  title: string;
  mtime: number;
  headless: boolean;
}

/**
 * 会话列表 store。
 *
 * ============ 与 session store 的分工 ============
 *  sessions（复数）= 列表 + 切换
 *  session（单数）  = 当前会话的元信息
 *  切换时：sessions.select(id) → 通知 session.applyInfo + messages.loadSnapshot
 * ============ ============
 */
export const useSessionsStore = defineStore('sessions', () => {
  const list = ref<SessionListItem[]>([]);
  const currentId = ref<string>('');
  const loading = ref(false);

  const current = computed(() => list.value.find((s) => s.id === currentId.value));

  /** 列表项的相对时间（供侧栏直接用） */
  function relativeTimeOf(item: SessionListItem): string {
    return formatRelativeTime(item.mtime);
  }

  async function load(): Promise<void> {
    loading.value = true;
    try {
      const res = await transport.request<SessionListItem[] | { items?: SessionListItem[] }>(
        'session.list',
        null
      );
      list.value = Array.isArray(res) ? res : (res?.items ?? []);
    } finally {
      loading.value = false;
    }
  }

  async function select(id: string): Promise<void> {
    if (id === currentId.value) return;
    currentId.value = id;

    const messages = useMessagesStore();
    const stream = useStreamStore();

    stream.endTurn();
    messages.clear();

    await transport.request('session.resume', { id });
    await messages.loadSnapshot();
  }

  async function createNew(): Promise<void> {
    const res = await transport.request<{ sessionId?: string }>('session.new', null);
    const messages = useMessagesStore();
    messages.clear();

    if (res?.sessionId) {
      currentId.value = res.sessionId;
      list.value.unshift({
        id: res.sessionId,
        title: '新会话',
        mtime: Date.now(),
        headless: false,
      });
    }
    await messages.loadSnapshot();
  }

  async function rename(id: string): Promise<void> {
    // P5 会用真正的输入对话框；P4 用 prompt 保证功能可用
    const target = list.value.find((s) => s.id === id);
    if (!target) return;
    const next = window.prompt('重命名会话', target.title);
    if (next === null) return;
    const trimmed = next.trim();
    if (!trimmed || trimmed === target.title) return;

    await transport.request('session.rename', { id, title: trimmed });
    target.title = trimmed;
  }

  async function duplicate(id: string): Promise<void> {
    await transport.request('session.duplicate', { id });
    await load();
  }

  async function remove(id: string): Promise<void> {
    const target = list.value.find((s) => s.id === id);
    if (!target) return;
    if (!window.confirm(`确定删除「${target.title}」？此操作不可恢复。`)) return;

    await transport.request('session.delete', { id });
    list.value = list.value.filter((s) => s.id !== id);

    if (currentId.value === id) {
      const first = list.value[0];
      if (first) await select(first.id);
      else await createNew();
    }
  }

  return {
    list,
    currentId,
    loading,
    current,
    relativeTimeOf,
    load,
    select,
    createNew,
    rename,
    duplicate,
    remove,
  };
});
