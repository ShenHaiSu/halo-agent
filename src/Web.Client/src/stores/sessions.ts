// src/Web.Client/src/stores/sessions.ts
// P5：分组/搜索/重命名(note)/删除二次确认在组件层；store 只做数据。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';
import { useMessagesStore } from './messages';
import { useStreamStore } from './stream';
import { usePermissionsStore } from './permissions';
import { useQueueStore } from './queue';
 import { useSessionStore } from './session';
 import { clearCollapsedTools } from '@/components/chat/toolMeta';
import { useToast } from '@/composables/useToast';
import { formatRelativeTime } from '@/utils/format';
import type { SessionSummary } from '@/contract/agent';
// P09-fix2 B：切换令牌。select 进入即 +1 并快照；任何异步返回后若令牌已变则直接丢弃，
// 保证快速连点 A→B→A 只保留最后一次，无旧快照覆盖新会话。
let switchToken = 0;
let switchDepth = 0;

/** 当前切换令牌（eventBridge 用它判定切换中过期帧）。 */
export function getSwitchToken(): number {
  return switchToken;
}

/** 是否正处在会话切换中（select/createNew 尚未结束）。 */
export function isSwitching(): boolean {
  return switchDepth > 0;
}

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
  const switching = ref(false);
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

  // P09-fix2 B：复用 resume.messages 直接绘制 + 切换令牌 + 全程 loading。
  // 失败语义保持：busy 直接抛 + Toast，不切 currentId、不清空。
  async function select(id: string): Promise<void> {
    if (id === currentId.value) return;
    const prev = currentId.value;
    const messages = useMessagesStore();
    const stream = useStreamStore();
    const perms = usePermissionsStore();
    const queue = useQueueStore();
    const sessionInfo = useSessionStore();
    const token = ++switchToken;
    switchDepth++;
    switching.value = true;
    messages.loading = true;
      // 切入即重置旧会话残留（C）：流式 / 询问卡 / 排队 / 摘要卡 / 折叠记忆（P11-E 单源）。
      stream.endTurn();
      // P23：切会话入口先清零水位线（估算态），再由快照覆盖；catch 分支同样保持已清零态。
      sessionInfo.resetWaterline();
      /* P15: turns store */
      perms.clear();
      queue.clear();
      clearCollapsedTools();
    try {
      let resumed: { count?: number; messages?: unknown } | null = null;
      try {
        resumed = await transport.request<{ count?: number; messages?: unknown; turns?: unknown }>('session.resume', { id });
      } catch (e) {
        const code = rpcCodeOf(e);
        // busy：直接抛 + Toast，不切 currentId、不清空当前页。
        if (code === 'state.busy') {
          useToast().info('有回合正在执行，稍后再切');
          currentId.value = prev;
          throw e;
        }
        // P08-fix 03：本地乐观空会话容错，降级为空白页 + toast。
        if (code === 'session.notFound') {
          const target = items.value.find((s) => s.id === id);
          const looksEmpty =
            !!target &&
            (target.title === '新会话' ||
              ((target.userMsgs ?? 0) === 0 && (target.assistantMsgs ?? 0) === 0));
          if (looksEmpty) {
            if (token !== switchToken) return;
            currentId.value = id;
            messages.clearItems();
            sessionInfo.compactionSummary = null; sessionInfo.compactionRemoved = null; sessionInfo.compactionKept = null;
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
      if (token !== switchToken) return;
      currentId.value = id;
      // 复用 resume.messages 直接绘制（无 seq 不回退游标，见 01）；缺字段才回退 loadSnapshot。
      const rawTurns = (resumed as Record<string,unknown>)?.turns as unknown;
      const rawMsgs = (resumed as Record<string,unknown>)?.messages as unknown;
      const msgLooksTurn = Array.isArray(rawMsgs) && rawMsgs.length > 0 && typeof rawMsgs[0] === 'object' && rawMsgs[0] !== null && ('turnId' in (rawMsgs[0] as object) || 'final' in (rawMsgs[0] as object));
      const isTurnShape = (Array.isArray(rawTurns) && (rawTurns.length===0 || (typeof rawTurns[0]==='object' && rawTurns[0] !== null && ('turnId' in (rawTurns[0] as object) || 'final' in (rawTurns[0] as object))))) || (!Array.isArray(rawTurns) && msgLooksTurn);
      const rawTurnsAsTurns = Array.isArray(rawTurns) && (rawTurns.length===0 || (typeof rawTurns[0]==='object' && rawTurns[0] !== null && ('turnId' in (rawTurns[0] as object) || 'final' in (rawTurns[0] as object))));
      const resumedIsFlat = !isTurnShape && Array.isArray(rawMsgs);
      const resumedTurns = rawTurnsAsTurns ? rawTurns as Array<Record<string, unknown>> : Array.isArray(rawMsgs) ? rawMsgs as Array<Record<string, unknown>> : null;
      if (resumedTurns) {
        messages.applySnapshot(resumedIsFlat ? { messages: resumedTurns as never } : { turns: resumedTurns as never });
      } else {
        await messages.loadSnapshot();
        if (token !== switchToken) return;
      }
       // 成功后补齐：resume 不带 session/agent 元信息；以 state.snapshot 全量补齐（含 agent.configured）。
       // P12-02：此前只补 session.id/cwd/historyPath，agent 恒为初始值导致切换器恒未配置。
       sessionInfo.compactionSummary = null; sessionInfo.compactionRemoved = null; sessionInfo.compactionKept = null;
       try {
         const snap = await transport.request<{ session?: Record<string, unknown>; agent?: Record<string, unknown>; usage?: Record<string, unknown> }>('state.snapshot', null);
         if (token !== switchToken) return;
         const s = snap?.session;
         if (s) sessionInfo.applyInfo({ sessionId: id, cwd: s.cwd, historyPath: s.historyPath });
         else sessionInfo.applyInfo({ sessionId: id });
         const ag = snap?.agent;
         if (ag && typeof ag === 'object') sessionInfo.applySnapshotAgent(ag);
          const us = snap?.usage as { inputTotal?: unknown; outputTotal?: unknown; lastPromptTokens?: unknown; lastCachedTokens?: unknown; estimatedInitialTokens?: unknown; sessionLlmCalls?: unknown } | undefined;
          if (us && typeof us === 'object') {
            sessionInfo.applyInfo({
              usage: {
                input: typeof us.inputTotal === 'number' ? us.inputTotal : 0,
                output: typeof us.outputTotal === 'number' ? us.outputTotal : 0,
              },
            });
            // P23：快照仅 session.id 切换采纳（此处 id 即新会话，全量覆盖；老引擎缺字段按 null/0）。
            sessionInfo.applySnapshotWaterline(
              {
                lastPromptTokens: typeof us.lastPromptTokens === 'number' ? us.lastPromptTokens : null,
                lastCachedTokens: typeof us.lastCachedTokens === 'number' ? us.lastCachedTokens : null,
                estimatedInitialTokens: typeof us.estimatedInitialTokens === 'number' ? us.estimatedInitialTokens : null,
                sessionLlmCalls: typeof us.sessionLlmCalls === 'number' ? us.sessionLlmCalls : 0,
              },
              id,
            );
          }
        } catch {
          sessionInfo.applyInfo({ sessionId: id });
        }
    } finally {
      switchDepth--;
      if (switchDepth <= 0) { switchDepth = 0; switching.value = false; }
      if (token === switchToken) messages.loading = false;
    }
  }

  async function createNew(): Promise<void> {
    const token = ++switchToken;
    switchDepth++;
    switching.value = true;
    const messages = useMessagesStore();
    const stream = useStreamStore();
    const perms = usePermissionsStore();
    const queue = useQueueStore();
    const sessionInfo = useSessionStore();
      messages.loading = true;
      stream.endTurn();
      // P23：新建入口先清零水位线，再由 loadSnapshot 覆盖。
      sessionInfo.resetWaterline();
      /* P15: turns store */
      perms.clear();
      queue.clear();
      clearCollapsedTools();
      sessionInfo.compactionSummary = null; sessionInfo.compactionRemoved = null; sessionInfo.compactionKept = null;
    try {
      const res = await transport.request<{ id?: string; sessionId?: string }>('session.new', null);
      if (token !== switchToken) return;
      messages.clearItems();
      const nid = res?.id ?? res?.sessionId ?? '';
      if (nid) {
        currentId.value = nid;
        items.value.unshift({ id: nid, title: '新会话', mtimeMs: Date.now(), headless: false });
        sessionInfo.applyInfo({ sessionId: nid });
      }
      await messages.loadSnapshot();
    } finally {
      switchDepth--;
      if (switchDepth <= 0) { switchDepth = 0; switching.value = false; }
      if (token === switchToken) messages.loading = false;
  }
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
    switching,
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
