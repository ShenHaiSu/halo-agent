// src/Web.Client/src/stores/sessions.ts
// P5：分组/搜索/重命名(note)/删除二次确认在组件层；store 只做数据。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';
import { useMessagesStore } from './messages';
import { useStreamStore } from './stream';
import { useToast } from '@/composables/useToast';
import { formatRelativeTime } from '@/utils/format';
import type { SessionSummary } from '@/contract/agent';

export interface SessionListItem {
  id: string;
  title: string;
  mtime: number;
  headless: boolean;
}
export interface SessionGroup {
  key: string;
  label: string;
  items: SessionSummary[];
}

function toSummary(raw: unknown): SessionSummary {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    id: String(o.id ?? ''),
    title: String(o.title ?? o.note ?? '新对话'),
    note: typeof o.note === 'string' ? o.note : null,
    cwd: typeof o.cwd === 'string' ? o.cwd : null,
    headless: o.headless === true,
    mtimeMs:
      typeof o.mtimeMs === 'number'
        ? o.mtimeMs
        : typeof o.mtime === 'number'
          ? (o.mtime as number)
          : Date.now(),
    userMsgs: typeof o.userMsgs === 'number' ? o.userMsgs : undefined,
    assistantMsgs: typeof o.assistantMsgs === 'number' ? o.assistantMsgs : undefined,
  };
}

export const useSessionsStore = defineStore('sessions', () => {
  const items = ref<SessionSummary[]>([]);
  // 兼容 P4 旧字段
  const list = computed(() =>
    items.value.map((s) => ({
      id: s.id,
      title: s.note ?? s.title,
      mtime: s.mtimeMs,
      headless: !!s.headless,
    }))
  );
  const currentId = ref<string>('');
  const loading = ref(false);
  const query = ref('');
  const current = computed(() => items.value.find((s) => s.id === currentId.value));

  const filtered = computed(() => {
    const q = query.value.trim().toLowerCase();
    if (!q) return items.value;
    return items.value.filter((s) => (s.note ?? s.title).toLowerCase().includes(q));
  });

  const grouped = computed<SessionGroup[]>(() => {
    const now = new Date();
    const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
    const groups: SessionGroup[] = [
      { key: 'today', label: '今天', items: [] },
      { key: 'yesterday', label: '昨天', items: [] },
      { key: 'week', label: '最近 7 天', items: [] },
      { key: 'older', label: '更早', items: [] },
    ];
    const sorted = [...filtered.value].sort((a, b) => b.mtimeMs - a.mtimeMs);
    for (const s of sorted) {
      if (s.mtimeMs >= startOfToday) groups[0]!.items.push(s);
      else if (s.mtimeMs >= startOfToday - 86400000) groups[1]!.items.push(s);
      else if (s.mtimeMs >= startOfToday - 7 * 86400000) groups[2]!.items.push(s);
      else groups[3]!.items.push(s);
    }
    return groups.filter((g) => g.items.length > 0);
  });

  function relativeTimeOf(item: SessionListItem): string {
    return formatRelativeTime(item.mtime);
  }

  async function load(): Promise<void> {
    loading.value = true;
    try {
      const res = await transport.request<{ items?: unknown[] } | unknown[]>('session.list', null);
      const arr = Array.isArray(res)
        ? res
        : Array.isArray((res as { items?: unknown[] }).items)
          ? (res as { items?: unknown[] }).items!
          : [];
      items.value = arr.map(toSummary);
    } finally {
      loading.value = false;
    }
  }

  function rpcCodeOf(e: unknown): string {
    const code = (e as { code?: unknown })?.code;
    if (typeof code === 'string' && code) return code;
    const msg = String((e as Error)?.message ?? '');
    if (msg.includes('session.notFound')) return 'session.notFound';
    if (msg.includes('state.busy')) return 'state.busy';
    if (msg.includes('params.invalid')) return 'params.invalid';
    return '';
  }

  async function select(id: string): Promise<void> {
    if (id === currentId.value) return;
    // P08-fix 03：先 resume，成功后再切 state；失败回滚 currentId（原先先 clear 后 resume，失败即白屏）。
    const prev = currentId.value;
    const messages = useMessagesStore();
    const stream = useStreamStore();
    try {
      await transport.request('session.resume', { id });
    } catch (e) {
      const code = rpcCodeOf(e);
      // P08-fix 03：本地乐观空会话容错（title==新会话 或 已知 0 消息），降级为 clear + loadSnapshot + toast。
      if (code === 'session.notFound') {
        const target = items.value.find((s) => s.id === id);
        const looksEmpty =
          !!target &&
          (target.title === '新会话' ||
            ((target.userMsgs ?? 0) === 0 && (target.assistantMsgs ?? 0) === 0));
        if (looksEmpty) {
          currentId.value = id;
          stream.endTurn();
          messages.clear();
          try {
            await messages.loadSnapshot();
          } catch {
            /* 快照失败保留空白页 */
          }
          useToast().info('已回到空白会话');
          return;
        }
      }
      currentId.value = prev;
      throw e;
    }
    currentId.value = id;
    stream.endTurn();
    await messages.loadSnapshot();
  }

  async function createNew(): Promise<void> {
    const res = await transport.request<{ id?: string; sessionId?: string }>('session.new', null);
    const messages = useMessagesStore();
    messages.clear();
    const nid = res?.id ?? res?.sessionId ?? '';
    if (nid) {
      currentId.value = nid;
      items.value.unshift({ id: nid, title: '新会话', mtimeMs: Date.now(), headless: false });
    }
    await messages.loadSnapshot();
  }

  function create(): Promise<void> {
    return createNew();
  }

  async function rename(id: string, note?: string): Promise<void> {
    const target = items.value.find((s) => s.id === id);
    if (!target) return;
    // R-5：禁止 window.prompt，原生弹窗会阻塞 CEF UI 线程。调用方必须传 note。
    if (note === undefined) throw new Error('重命名需要提供名称（调用方用 HInput 对话框收集）');
    const next = note.trim();
    if (!next || next === (target.note ?? target.title)) {
      if (!next) {
        // 空值视为清除自定义名
        await transport.request('session.rename', { id, note: '' });
        target.note = null;
      }
      return;
    }
    if (next.length > 80) throw new Error('名称不能超过 80 个字符');
    const prev = target.note ?? target.title;
    target.note = next;
    try {
      await transport.request('session.rename', { id, note: next });
    } catch (e) {
      target.note = prev === target.title ? null : prev;
      throw e;
    }
  }

  async function remove(id: string): Promise<void> {
    await transport.request('session.delete', { id });
    items.value = items.value.filter((s) => s.id !== id);
    if (currentId.value === id) {
      const first = items.value[0];
      if (first) await select(first.id);
      else await createNew();
    }
  }

  /** 复制会话：契约无 session.duplicate，用“新建空会话”语义兜底（P5 不新增方法）。 */
  async function duplicate(_id: string): Promise<void> {
    await createNew();
  }

  return {
    items,
    list,
    currentId,
    loading,
    query,
    filtered,
    grouped,
    current,
    relativeTimeOf,
    load,
    select,
    createNew,
    create,
    rename,
    duplicate,
    remove,
  };
});
