import { defineStore } from 'pinia';
import { ref, computed, watch } from 'vue';
import {
  getHostBridge,
  haloEventBus,
  type WorkspaceInfo,
  type SessionMetadata,
  type UnifiedChatMessage,
  type ContextBudgetStatusDto,
  type UiActiveState,
} from '@shared/bridge/hostBridge';
import type {
  TurnSummary,
  TimelineBlockContent,
  TimelineBlockContentsResponse,
  TimelineToolResult,
  HistoryBranchResult,
} from '@shared/bridge/hostBridge';
import { historyErrorMessage } from '@shared/bridge/protocol/errors';
import { useConfigStore } from '@/stores/configStore';
import { useSessionStore } from '@/stores/sessionStore';
import { isBadTime } from '@shared/utils/timeFormat';
import { instantTitleFromUserText, parseTitleResult, refineTitleLocal, truncateRoundText } from '@shared/utils/sessionTitle';
import { dropPendingUser, turns } from '@features/agent/stores/model';
import type { PendingUser } from '@features/session/utils/pendingUser';
import { isPendingExpired, matchConfirmedBySummary } from '@features/session/utils/pendingUser';
import { readNeed02Flags } from '@features/need02/utils/need02Flags';
import { debug as need02Debug } from '@features/need02/utils/need02Debug';
export const useWorkspaceStore = defineStore('workspace', () => {
  const configStore = useConfigStore();

  const workspaces = ref<WorkspaceInfo[]>([]);
  const activeWorkspaceId = ref<string>('');

  const sessions = ref<SessionMetadata[]>([]);
  const activeSessionId = ref<string>('');
  // need22-P2 F1：activeSessionId 唯一真相=sessionStore；本 ref 为兼容镜像，任何写入同步过去。
  const sessionStore = useSessionStore();
  watch(
    activeSessionId,
    (sid) => {
      sessionStore.syncActive(sid || '');
    },
    { flush: 'sync' }
  );
  const sessionMessages = ref<UnifiedChatMessage[]>([]);
  const timelineSummaries = ref<TurnSummary[]>([]);
  const blockContentsCache = ref<Record<string, TimelineBlockContent>>({});
  const toolResultCache = ref<Record<string, TimelineToolResult>>({});
  /** need02-P1：乐观 overlay（仅视图层，不写 jsonl）。p1 off 时恒回 sessionMessages（旧链）。 */
  function isP1On(): boolean {
    try {
      return readNeed02Flags().p1;
    } catch {
      return true;
    }
  }
  /** 摘要源精确对账（requestId/lastRequestId），命中即转正并清 pending。 */
  function confirmPendingHit(sid: string, p: PendingUser, confirmedId: string): void {
    need02Debug('pending-confirmed', { requestId: p.requestId, key: p.key, confirmedId });
    dropPendingUser(sid);
  }
  function tryConfirmPending(sid: string): void {
    try {
      if (!isP1On()) return;
      const id = (sid || '').trim();
      if (!id) return;
      const t = turns.value[id];
      const p = t?.pendingUser;
      if (!p || (p.status !== 'sending' && p.status !== 'cancelled')) return;
      if (p.status === 'sending' && isPendingExpired(p)) {
        try {
          need02Debug('pending-expired-drop', { requestId: p.requestId });
        } catch {
          /* ignore */
        }
        dropPendingUser(id);
        return;
      }
      // 摘要源 requestId/lastRequestId 精确对账；cancelled 态命中同样转正。
      const summaryHit = matchConfirmedBySummary(p, timelineSummaries.value);
      if (summaryHit) {
        confirmPendingHit(id, p, String(p.requestId || ''));
      }
    } catch {
      /* 对账永不阻断 */
    }
  }
  const budgetStatus = ref<ContextBudgetStatusDto | null>(null);
  // need22-P3 G6：budget 失败显式角标（顶栏⚠），统一 {code,message}
  const budgetError = ref<{ code: string; message: string } | null>(null);
  const searchQuery = ref<string>('');
  const loading = ref<boolean>(false);
  const loadingMessages = ref<boolean>(false);
  // UI活跃状态：后端config/ui-state.json持久（上次离开时的工作区+会话，只恢复到main）
  const lastActive = ref<UiActiveState>({ workspaceId: null, sessionId: null });
  let lastActiveLoaded = false;
  let persistTimer: number | null = null;
  async function ensureLastActive(): Promise<UiActiveState> {
    if (lastActiveLoaded) return lastActive.value;
    lastActiveLoaded = true;
    try {
      const res = await getHostBridge().getLastActive();
      if (res.ok && res.data) lastActive.value = { workspaceId: res.data.workspaceId ?? null, sessionId: res.data.sessionId ?? null };
    } catch {
      /* 无/坏一律回空，不阻断启动 */
    }
    return lastActive.value;
  }
  function persistLastActive(): void {
    try {
      if (persistTimer) window.clearTimeout(persistTimer);
      persistTimer = window.setTimeout(() => {
        const wid = activeWorkspaceId.value || '';
        const sid = activeSessionId.value || '';
        if (!wid) return;
        // Promise.resolve 包裹：桥被 mock 或异常实现返回非 Promise 时，
        // 直接 .catch 会在 teardown 后的 timer 里抛未捕获异常（全量并发下已复现）。
        void Promise.resolve(getHostBridge().setLastActive(wid, sid)).catch(() => {
          /* 静默：持久失败不阻断 */
        });
      }, 300);
    } catch {
      /* ignore */
    }
  }
  /** 只恢复到main：sub一律解析到其父main，父缺失则回首个 */
  function resolveMainSessionId(list: SessionMetadata[], candidateId: string | null): string {
    if (!list.length) return '';
    if (candidateId) {
      const hit = list.find((s) => s.id === candidateId);
      if (hit) {
        const role = String((hit as unknown as { agentRole?: string }).agentRole || 'main').toLowerCase();
        const parent = String((hit as unknown as { parentSessionId?: string }).parentSessionId || '');
        if (role !== 'sub' || !parent) return hit.id;
        const p = list.find((s) => s.id === parent);
        if (p) return p.id;
      }
    }
    return list[0].id;
  }
  // need03-03：自动标题冻结位（手动改名最高优，此后永不覆盖）+ 首轮已生成位 + 生成中位
  const titleLocked = ref<Record<string, boolean>>({});
  const autoTitleDone = ref<Record<string, boolean>>({});
  const titleGenerating = ref<Record<string, boolean>>({});

  const activeWorkspace = computed<WorkspaceInfo | null>(() => {
    return workspaces.value.find((w) => w.id === activeWorkspaceId.value) || workspaces.value[0] || null;
  });

  /** 工作区展示名：alias 优先（纯视觉），空则回退 name。 */
  function workspaceLabel(ws: WorkspaceInfo | null | undefined): string {
    if (!ws) return '';
    return (ws.alias && ws.alias.trim()) || ws.name || '';
  }

  const activeSession = computed<SessionMetadata | null>(() => {
    return sessions.value.find((s) => s.id === activeSessionId.value) || sessions.value[0] || null;
  });

  /** 历史只读横幅已随统一仓储退役：现全部会话可写，旧格式由打开时错误码提示。 */
  const isHistoryReadonly = computed<boolean>(() => false);

  const filteredSessions = computed<SessionMetadata[]>(() => {
    const q = searchQuery.value.trim().toLowerCase();
    if (!q) return sessions.value;
    return sessions.value.filter(
      (s) => s.title.toLowerCase().includes(q) || s.sessionHash.toLowerCase().includes(q) || s.id.toLowerCase().includes(q)
    );
  });

  const pinnedSessions = computed<SessionMetadata[]>(() => {
    return filteredSessions.value.filter((s) => s.isPinned);
  });

  const unpinnedSessions = computed<SessionMetadata[]>(() => {
    return filteredSessions.value.filter((s) => !s.isPinned);
  });

  async function loadWorkspaces(): Promise<void> {
    loading.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getWorkspaces();
      if (res.ok && res.data) {
        workspaces.value = res.data;
        const saved = await ensureLastActive();
        if (!activeWorkspaceId.value || !workspaces.value.some((w) => w.id === activeWorkspaceId.value)) {
          const savedWid = (saved.workspaceId || '').trim();
          if (savedWid && workspaces.value.some((w) => w.id === savedWid)) {
            activeWorkspaceId.value = savedWid;
          } else if (workspaces.value.length > 0) {
            activeWorkspaceId.value = workspaces.value[0].id;
          }
        }
      }
    } catch (err) {
      console.error('[workspaceStore] loadWorkspaces failed:', err);
    } finally {
      loading.value = false;
    }
  }

  async function selectWorkspace(workspaceId: string): Promise<void> {
    if (activeWorkspaceId.value === workspaceId && sessions.value.length > 0) {
      return;
    }
    activeWorkspaceId.value = workspaceId;
    await loadSessions(workspaceId);
    persistLastActive();
  }

  async function addWorkspace(rootPath: string, name?: string): Promise<WorkspaceInfo | null> {
    try {
      const bridge = getHostBridge();
      const res = await bridge.addWorkspace(rootPath, name);
      if (res.ok && res.data) {
        await loadWorkspaces();
        await selectWorkspace(res.data.id);
        return res.data;
      }
    } catch (err) {
      console.error('[workspaceStore] addWorkspace failed:', err);
    }
    return null;
  }

  async function browseAndAddWorkspace(): Promise<WorkspaceInfo | null> {
    try {
      const bridge = getHostBridge();
      const pathRes = await bridge.browseWorkspaceFolder();
      if (pathRes.ok && pathRes.data) {
        return await addWorkspace(pathRes.data);
      }
    } catch (err) {
      console.error('[workspaceStore] browseAndAddWorkspace failed:', err);
    }
    return null;
  }

  async function removeWorkspace(workspaceId: string): Promise<boolean> {
    try {
      const bridge = getHostBridge();
      const res = await bridge.removeWorkspace(workspaceId);
      if (res.ok && res.data) {
        workspaces.value = workspaces.value.filter((w) => w.id !== workspaceId);
        if (activeWorkspaceId.value === workspaceId) {
          activeWorkspaceId.value = workspaces.value[0]?.id || '';
          activeSessionId.value = '';
          if (activeWorkspaceId.value) {
            await loadSessions(activeWorkspaceId.value);
          } else {
            sessions.value = [];
            sessionMessages.value = [];
            timelineSummaries.value = [];
            budgetStatus.value = null;
            budgetError.value = null;
          }
        }
        persistLastActive();
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] removeWorkspace failed:', err);
    }
    return false;
  }

  /**
   * 设置/清除工作区别名（纯前端视觉，不进提示词/请求头/上下文）。
   * alias 传空串 = 清除别名（回退显示文件夹名）。成功后只就地更新列表项，不重拉、不打断当前会话。
   */
  async function renameWorkspace(workspaceId: string, alias: string): Promise<boolean> {
    const wid = (workspaceId || '').trim();
    if (!wid) return false;
    try {
      const bridge = getHostBridge();
      const res = await bridge.renameWorkspace(wid, alias.trim());
      if (res.ok && res.data) {
        const idx = workspaces.value.findIndex((w) => w.id === wid);
        if (idx >= 0) {
          const merged = { ...workspaces.value[idx], ...res.data };
          merged.displayName = (merged.alias && merged.alias.trim()) || merged.name;
          workspaces.value[idx] = merged;
          workspaces.value = [...workspaces.value];
        }
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] renameWorkspace failed:', err);
    }
    return false;
  }

  /** need04-01：记录必达：内存态秒级刷新时间并按置顶/活跃重排（与后端同序）。 */
  function touchSessionTime(sessionId: string, iso?: string): void {
    const sid = (sessionId || '').trim();
    if (!sid) return;
    const item = sessions.value.find((s) => s.id === sid);
    if (!item) return;
    const nowIso = iso && !isBadTime(iso) ? iso : new Date().toISOString();
    (item as { updatedAtUtc: string }).updatedAtUtc = nowIso;
    if (isBadTime((item as unknown as { createdAtUtc: string }).createdAtUtc)) {
      (item as unknown as { createdAtUtc: string }).createdAtUtc = nowIso;
    }
    sessions.value = [...sessions.value].sort((a, b) => {
      if (!!a.isPinned !== !!b.isPinned) return (b.isPinned ? 1 : 0) - (a.isPinned ? 1 : 0);
      return new Date(b.updatedAtUtc).getTime() - new Date(a.updatedAtUtc).getTime();
    });
  }

  /** need04-01：轮末轻量对账：只合并时间/条数/标题，不替换数组引用防滚动跳动。每轮至多1次。 */
  async function reconcileSessionTimes(): Promise<void> {
    if (!activeWorkspaceId.value) return;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getSessions(activeWorkspaceId.value);
      if (!res.ok || !res.data) return;
      const incoming = new Map(res.data.map((s) => [s.id, s]));
      for (const cur of sessions.value) {
        const nxt = incoming.get(cur.id);
        if (!nxt) continue;
        if (nxt.updatedAtUtc && !isBadTime(nxt.updatedAtUtc)) (cur as { updatedAtUtc: string }).updatedAtUtc = nxt.updatedAtUtc;
        if (nxt.createdAtUtc && !isBadTime(nxt.createdAtUtc)) (cur as unknown as { createdAtUtc: string }).createdAtUtc = nxt.createdAtUtc;
        if (typeof nxt.messageCount === 'number') cur.messageCount = nxt.messageCount;
        if (nxt.title && nxt.title.trim()) cur.title = nxt.title;
      }
      sessions.value = [...sessions.value].sort((a, b) => {
        if (!!a.isPinned !== !!b.isPinned) return (b.isPinned ? 1 : 0) - (a.isPinned ? 1 : 0);
        return new Date(b.updatedAtUtc).getTime() - new Date(a.updatedAtUtc).getTime();
      });
    } catch {
      /* 对账失败只打日志，不阻断 */
    }
  }
  async function loadSessions(workspaceId: string): Promise<void> {
    if (!workspaceId) {
      sessions.value = [];
      activeSessionId.value = '';
      sessionMessages.value = [];
      timelineSummaries.value = [];
      budgetStatus.value = null;
      budgetError.value = null;
      return;
    }

    try {
      const bridge = getHostBridge();
      const res = await bridge.getSessions(workspaceId);
      if (res.ok && res.data) {
        const nowIso = new Date().toISOString();
        // need04-01：坏时间统一回填（空串/null/0001/Invalid/年<2000），内存态不回写磁盘
        for (const s of res.data) {
          if (isBadTime((s as unknown as { createdAtUtc: string }).createdAtUtc))
            (s as unknown as { createdAtUtc: string }).createdAtUtc = nowIso;
          if (isBadTime((s as unknown as { updatedAtUtc: string }).updatedAtUtc))
            (s as unknown as { updatedAtUtc: string }).updatedAtUtc = (s.createdAtUtc as string) || nowIso;
          const c = new Date((s as unknown as { createdAtUtc: string }).createdAtUtc).getTime();
          const u = new Date((s as unknown as { updatedAtUtc: string }).updatedAtUtc).getTime();
          if (!isNaN(c) && !isNaN(u) && u < c)
            (s as unknown as { updatedAtUtc: string }).updatedAtUtc = (s as unknown as { createdAtUtc: string }).createdAtUtc;
        }
        sessions.value = res.data;
        if (!activeSessionId.value || !sessions.value.some((s) => s.id === activeSessionId.value)) {
          // 启动恢复：同工作区才用存档session，且只到main（sub解析到父main）
          const saved = await ensureLastActive();
          const candidate = saved.workspaceId === workspaceId ? saved.sessionId : null;
          activeSessionId.value = resolveMainSessionId(sessions.value, candidate);
        }

        if (activeSessionId.value) {
          await loadSessionMessages(activeSessionId.value);
          // 启动/工作区进入：同样做一次孤儿愈合，脏了才重拉
          if (await healSessionHistory()) await loadSessionMessages(activeSessionId.value);
          await refreshBudget();
          maybeRefineTitleAfterLoad(activeSessionId.value);
        } else {
          sessionMessages.value = [];
          timelineSummaries.value = [];
          budgetStatus.value = null;
          budgetError.value = null;
        }
      }
    } catch (err) {
      console.error('[workspaceStore] loadSessions failed:', err);
    }
  }

  async function selectSession(sessionId: string): Promise<void> {
    const sid = (sessionId || '').trim();
    if (!sid) return;
    if (activeSessionId.value === sid && useSessionStore().activeSessionId === sid) return;
    // need22-P2 F3：递增 epoch + 换发 AbortController；旧 epoch 响应全部丢弃
    const sessionStore = useSessionStore();
    const { epoch, signal } = sessionStore.beginSelect(sid);
    activeSessionId.value = sid;
    await loadSessionMessages(sid, { signal, epoch });
    // 进会话检测：孤儿 tool_calls 就地愈合（中断/杀进程残留），脏了才重拉
    if (await healSessionHistory({ signal, epoch })) await loadSessionMessages(sid, { signal, epoch });
    await refreshBudget({ signal, epoch });
    if (sessionStore.isCurrent(epoch, signal) && sessionStore.activeSessionId === sid) {
      maybeRefineTitleAfterLoad(sid);
    }
    persistLastActive();
  }

  async function createSession(title?: string, providerId?: string, modelId?: string): Promise<SessionMetadata | null> {
    if (!activeWorkspaceId.value) return null;

    try {
      const bridge = getHostBridge();
      const pId = providerId || configStore.activeProvider?.id;
      const mId = modelId || configStore.activeModel?.id;
      const res = await bridge.createSession(activeWorkspaceId.value, title, pId, mId);
      if (res.ok && res.data) {
        const newSess = res.data;
        if (isBadTime((newSess as unknown as { createdAtUtc: string }).createdAtUtc))
          (newSess as unknown as { createdAtUtc: string }).createdAtUtc = new Date().toISOString();
        if (isBadTime((newSess as unknown as { updatedAtUtc: string }).updatedAtUtc))
          (newSess as unknown as { updatedAtUtc: string }).updatedAtUtc = (newSess as unknown as { createdAtUtc: string }).createdAtUtc;
        sessions.value = [newSess, ...sessions.value];
        touchSessionTime(newSess.id, (newSess as unknown as { createdAtUtc: string }).createdAtUtc);
        // A全量：新建即换发 epoch，中断旧会话在途回包，旧 epoch 响应全部丢弃不覆盖新会话视图
        const sessionStore = useSessionStore();
        const { epoch, signal } = sessionStore.beginSelect(newSess.id);
        activeSessionId.value = newSess.id;
        // A全量：同步清空历史/摘要/缓存/预算，视图立即进空态，不等异步回包
        sessionMessages.value = [];
        timelineSummaries.value = [];
        budgetStatus.value = null;
        budgetError.value = null;
        try {
          clearTimelineCaches();
        } catch {
          /* cache clear never blocks */
        }
        await refreshBudget({ signal, epoch });
        persistLastActive();
        return newSess;
      }
    } catch (err) {
      console.error('[workspaceStore] createSession failed:', err);
    }
    return null;
  }

  async function renameSession(sessionId: string, newTitle: string, opts?: { manual?: boolean }): Promise<boolean> {
    if (!activeWorkspaceId.value || !newTitle.trim()) return false;
    try {
      const bridge = getHostBridge();
      const res = await bridge.updateSessionTitle(activeWorkspaceId.value, sessionId, newTitle.trim());
      if (res.ok && res.data) {
        const item = sessions.value.find((s) => s.id === sessionId);
        if (item) {
          item.title = newTitle.trim();
          touchSessionTime(item.id);
        }
        // need03-03：显式改名（默认视为手动）即永久冻结自动逻辑；LLM 回写走 manual:false
        if (opts?.manual !== false) {
          titleLocked.value[sessionId] = true;
          autoTitleDone.value[sessionId] = true;
        }
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] renameSession failed:', err);
    }
    return false;
  }

  async function togglePinSession(sessionId: string, isPinned: boolean): Promise<boolean> {
    if (!activeWorkspaceId.value) return false;
    try {
      const bridge = getHostBridge();
      const res = await bridge.setSessionPinned(activeWorkspaceId.value, sessionId, isPinned);
      if (res.ok && res.data) {
        const item = sessions.value.find((s) => s.id === sessionId);
        if (item) {
          item.isPinned = isPinned;
          touchSessionTime(item.id);
        }
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] togglePinSession failed:', err);
    }
    return false;
  }

  async function deleteSession(sessionId: string): Promise<boolean> {
    if (!activeWorkspaceId.value) return false;
    try {
      const bridge = getHostBridge();
      const res = await bridge.deleteSession(activeWorkspaceId.value, sessionId);
      if (res.ok && res.data) {
        sessions.value = sessions.value.filter((s) => s.id !== sessionId);
        if (activeSessionId.value === sessionId) {
          if (sessions.value.length > 0) {
            await selectSession(sessions.value[0].id);
          } else {
            activeSessionId.value = '';
            sessionMessages.value = [];
            timelineSummaries.value = [];
            budgetStatus.value = null;
            budgetError.value = null;
            persistLastActive();
          }
        }
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] deleteSession failed:', err);
    }
    return false;
  }

  async function loadSessionMessages(
    sessionId: string,
    opts?: { signal?: AbortSignal | null; epoch?: number; preserveBudget?: boolean; background?: boolean }
  ): Promise<void> {
    if (!activeWorkspaceId.value || !sessionId) {
      sessionMessages.value = [];
      timelineSummaries.value = [];
      return;
    }
    try {
      clearTimelineCaches();
    } catch {
      /* cache clear never blocks */
    }
    // need22-P2 F3：无显式 epoch 时按进入时 epoch 快照；旧 epoch 响应丢弃不覆盖视图
    const sessionStore = useSessionStore();
    const epoch = opts?.epoch ?? sessionStore.epoch;
    const signal = opts?.signal ?? null;
    const sid = (sessionId || '').trim();
    // 默认只加载当前会话；后台加载必须显式声明，禁止异步响应清空当前视图。
    if (!opts?.background && sid !== activeSessionId.value) return;
    // 发送链保留旧预算，切会话仍清空：消息列表照旧清空防闪旧历史
    sessionMessages.value = [];
    timelineSummaries.value = [];
    if (!opts?.preserveBudget) {
      budgetStatus.value = null;
      budgetError.value = null;
    }
    loadingMessages.value = true;
    try {
      const bridge = getHostBridge();
      const api = bridge as unknown as { getTimelineSummary?: (w: string, s: string) => Promise<{ ok: boolean; data?: TurnSummary[] }> };
      const res =
        typeof api.getTimelineSummary === 'function'
          ? await api.getTimelineSummary(activeWorkspaceId.value, sid)
          : { ok: false as const, data: undefined };
      if (!sessionStore.isCurrent(epoch, signal) || sessionStore.activeSessionId !== sid) {
        sessionStore.discardStale(`loadSessionMessages(${sid.slice(-6)})`);
        return;
      }
      if (res.ok && Array.isArray(res.data)) {
        timelineSummaries.value = res.data;
      } else {
        timelineSummaries.value = [];
      }
      // 首屏摘要直达：不再拉全量消息；旧全量字段保留为待删死代码，收尾阶段统一清理。
      sessionMessages.value = [];
      // 首次回包对账转正（key 不变，只换 id/时间；摘要回包后直接对账）。
      tryConfirmPending(sid);
    } catch (err) {
      if (!sessionStore.isCurrent(epoch, signal)) {
        sessionStore.discardStale(`loadSessionMessages-error(${sid.slice(-6)})`);
        return;
      }
      console.error('[workspaceStore] loadSessionMessages failed:', err);
      sessionMessages.value = [];
    } finally {
      loadingMessages.value = false;
    }
  }

  async function loadTimelineSummary(sessionId: string): Promise<void> {
    const wid = activeWorkspaceId.value || '';
    const sid = (sessionId || '').trim();
    if (!wid || !sid) {
      timelineSummaries.value = [];
      return;
    }
    try {
      const bridge = getHostBridge();
      const api = bridge as unknown as { getTimelineSummary?: (w: string, s: string) => Promise<{ ok: boolean; data?: TurnSummary[] }> };
      if (typeof api.getTimelineSummary !== 'function') {
        timelineSummaries.value = [];
        return;
      }
      const res = await api.getTimelineSummary(wid, sid);
      if (res && res.ok && Array.isArray(res.data)) {
        timelineSummaries.value = res.data;
      } else {
        timelineSummaries.value = [];
      }
    } catch {
      timelineSummaries.value = [];
    }
  }

  async function getTurnBlockIds(turnId: string): Promise<string[]> {
    const wid = activeWorkspaceId.value || '';
    const sid = activeSessionId.value || '';
    const tid = (turnId || '').trim();
    if (!wid || !sid || !tid) return [];
    try {
      const bridge = getHostBridge();
      const api = bridge as unknown as { getTurnBlockIds?: (w: string, s: string, t: string) => Promise<{ ok: boolean; data?: string[] }> };
      if (typeof api.getTurnBlockIds !== 'function') return [];
      const res = await api.getTurnBlockIds(wid, sid, tid);
      if (res && res.ok && Array.isArray(res.data)) return res.data;
    } catch {
      /* miss -> empty, caller shows placeholder */
    }
    return [];
  }

  async function getBlockContents(blockIds: string[]): Promise<{ items: Record<string, TimelineBlockContent>; missing: string[] }> {
    const wid = activeWorkspaceId.value || '';
    const sid = activeSessionId.value || '';
    const ids = Array.isArray(blockIds) ? blockIds.filter((s) => s && s.trim()) : [];
    const items: Record<string, TimelineBlockContent> = {};
    const uncached: string[] = [];
    for (const id of ids) {
      const hit = blockContentsCache.value[id];
      if (hit) items[id] = hit;
      else if (!uncached.includes(id)) uncached.push(id);
    }
    let missing: string[] = ids.filter((id) => !items[id]);
    if (uncached.length > 0 && wid && sid) {
      try {
        const bridge = getHostBridge();
        const api = bridge as unknown as {
          getBlockContents?: (w: string, s: string, b: string[]) => Promise<{ ok: boolean; data?: TimelineBlockContentsResponse }>;
        };
        if (typeof api.getBlockContents === 'function') {
          const res = await api.getBlockContents(wid, sid, uncached);
          if (res && res.ok && res.data) {
            const fetched = res.data.items || {};
            for (const [k, v] of Object.entries(fetched)) {
              items[k] = v as TimelineBlockContent;
              blockContentsCache.value = { ...blockContentsCache.value, [k]: v as TimelineBlockContent };
            }
            missing = [...(res.data.missing || [])];
            for (const id of uncached) {
              if (!items[id] && !missing.includes(id)) missing.push(id);
            }
          }
        }
      } catch {
        /* fetch fail -> treat uncached as missing */
      }
    }
    return { items, missing };
  }

  async function getToolResult(turnId: string, callId: string): Promise<TimelineToolResult | null> {
    const wid = activeWorkspaceId.value || '';
    const sid = activeSessionId.value || '';
    const tid = (turnId || '').trim();
    const cid = (callId || '').trim();
    if (!wid || !sid || !tid || !cid) return null;
    const key = `${tid}::${cid}`;
    const hit = toolResultCache.value[key];
    if (hit) return hit;
    try {
      const bridge = getHostBridge();
      const api = bridge as unknown as {
        getToolResult?: (w: string, s: string, t: string, c: string) => Promise<{ ok: boolean; data?: TimelineToolResult }>;
      };
      if (typeof api.getToolResult !== 'function') return null;
      const res = await api.getToolResult(wid, sid, tid, cid);
      if (res && res.ok && res.data) {
        toolResultCache.value = { ...toolResultCache.value, [key]: res.data };
        return res.data;
      }
    } catch {
      /* miss -> null */
    }
    return null;
  }

  function clearTimelineCaches(): void {
    blockContentsCache.value = {};
    toolResultCache.value = {};
  }

  async function appendMessage(msg: UnifiedChatMessage): Promise<boolean> {
    if (!activeWorkspaceId.value || !activeSessionId.value) return false;
    const sid = activeSessionId.value;
    const wid = activeWorkspaceId.value;
    try {
      const bridge = getHostBridge();
      const res = await bridge.appendSessionMessage(wid, sid, msg);
      if (res.ok && res.data) {
        sessionMessages.value.push(msg);
        const cur = sessions.value.find((s) => s.id === sid) || activeSession.value;
        if (cur) {
          cur.messageCount = sessionMessages.value.length;
          cur.updatedAtUtc = new Date().toISOString();
          // 首条 user 发出即取首句前 5 字作为即时标题；落盘由后端追加消息流程完成，
          // 不走改名通道，以免提前把标题锁死、导致首轮结束后的精炼被短路。
          if (
            msg.role === 'user' &&
            !titleLocked.value[sid] &&
            !autoTitleDone.value[sid] &&
            (cur.title === '新建会话' || !cur.title?.trim()) &&
            msg.content
          ) {
            cur.title = instantTitleFromUserText(String(msg.content));
          }
        }
        await refreshBudget();
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] appendMessage failed:', err);
    }
    return false;
  }
  /**
   * need23-R1：把后端已落盘的单条消息增量合并进 sessionMessages（不触发全量重拉）。
   * 幂等：同 id 已存在则跳过；仅当前会话才写视图。返回是否真的插入。
   */
  function mergePersistedMessage(sessionId: string, msg: UnifiedChatMessage): boolean {
    const sid = (sessionId || '').trim();
    if (!sid || !msg || !msg.id) return false;
    if (sid !== activeSessionId.value) return false;
    const list = sessionMessages.value;
    if (list.some((m) => String(m.id) === String(msg.id))) return false;
    // 新数组引用 → 触发 watcher；投影层按内容指纹命中历史前缀，只重建尾部
    sessionMessages.value = [...list, msg];
    const cur = sessions.value.find((s) => s.id === sid) || activeSession.value;
    if (cur) cur.messageCount = sessionMessages.value.length;
    return true;
  }
  // 首轮文本取数：以后端落盘为准（轮末重拉会同步清空内存视图），失败回退内存
  async function resolveFirstRoundTexts(sid: string): Promise<{ firstUser: string; assistant: string; userCount: number }> {
    let firstUser = '';
    let assistant = '';
    let userCount = 0;
    let countedRemotely = false;
    try {
      const wid = activeWorkspaceId.value;
      if (wid) {
        const bridge = getHostBridge();
        // need03-03-B：标题链路统一走后端直读（v2 会话 getSessionMessages 拒服 USE_TIMELINE_API，
        // 老后端缺方法时 typeof 守卫回退旧链路）。
        const directApi = bridge as unknown as {
          getFirstRoundTexts?: (
            w: string,
            s: string
          ) => Promise<{
            ok: boolean;
            code?: string;
            message?: string;
            data?: { firstUser?: unknown; assistant?: unknown; userCount?: unknown };
          }>;
        };
        if (typeof directApi.getFirstRoundTexts === 'function') {
          const direct = await directApi.getFirstRoundTexts(wid, sid);
          if (direct && direct.ok && direct.data) {
            firstUser = String(direct.data.firstUser ?? '');
            assistant = String(direct.data.assistant ?? '');
            const n = Number(direct.data.userCount ?? 0);
            userCount = Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
            countedRemotely = true;
            // DEBUG-TITLE: 临时诊断日志，定位“标题生成失败”后删除
            console.debug('[title-debug] firstRound via getFirstRoundTexts', {
              sid,
              userCount,
              firstLen: firstUser.trim().length,
              assistantLen: assistant.trim().length,
            });
          } else if (direct && !direct.ok) {
            // DEBUG-TITLE: 临时诊断日志，定位“标题生成失败”后删除
            console.debug('[title-debug] getFirstRoundTexts not ok, fallback legacy', { sid, code: direct.code, message: direct.message });
          }
        }
        if (!countedRemotely) {
          const remoteApi = bridge as unknown as {
            getSessionMessages?: (w: string, s: string) => Promise<{ ok: boolean; data?: UnifiedChatMessage[] }>;
          };
          if (typeof remoteApi.getSessionMessages === 'function') {
            const remote = await bridge.getSessionMessages(wid, sid);
            if (remote && remote.ok && Array.isArray(remote.data)) {
              const msgs = remote.data as UnifiedChatMessage[];
              const remoteUsers = msgs.filter((m) => m.role === 'user');
              userCount = remoteUsers.length;
              countedRemotely = true;
              const firstRemote = remoteUsers[0];
              if (firstRemote) {
                firstUser = String(firstRemote.content || '');
                const lastAssistant = [...msgs].reverse().find((m) => m.role === 'assistant');
                assistant = String(lastAssistant?.content || '');
              }
            }
          }
        }
      }
    } catch {
      /* 落盘读取失败则回退内存计数 */
    }
    if (!countedRemotely) {
      const memUsers = sessionMessages.value.filter((m) => m.role === 'user');
      userCount = memUsers.length;
      const firstMem = memUsers[0];
      if (firstMem) {
        firstUser = String(firstMem.content || '');
        const lastAssistant = [...sessionMessages.value].reverse().find((m) => m.role === 'assistant');
        assistant = String(lastAssistant?.content || '');
      }
    }
    return { firstUser, assistant, userCount };
  }
  // need03-03 T2：首轮响应结束后精炼一次（同模型小请求优先，失败则本地精炼兜底；手动锁/已生成/生成中直接跳过）
  async function generateTitleAfterFirstRound(sessionId: string): Promise<void> {
    const sid = (sessionId || '').trim();
    if (!sid || titleLocked.value[sid] || autoTitleDone.value[sid] || titleGenerating.value[sid]) return;
    const sess = sessions.value.find((s) => s.id === sid);
    if (!sess) return;
    // 子会话标题由后端锁定展示，不参与自动精炼
    const sessRole = String((sess as unknown as { agentRole?: string }).agentRole || '').toLowerCase();
    if (sessRole === 'sub') return;
    // 仅当前会话触发，跨会话事件丢弃，避免后台覆盖当前视图
    if (sid !== activeSessionId.value) return;
    // 轮末重拉会同步清空内存视图，计数以后端落盘为准，避免把“没数到”误判成非首轮
    const { firstUser: firstUserText, assistant: assistantText, userCount } = await resolveFirstRoundTexts(sid);
    if (userCount === 0) {
      // 视图尚未就位或落盘尚未可见：跳过本次不冻结，等待下次进入补跑
      return;
    }
    if (userCount > 1) {
      // 真多轮才冻结，此后不再尝试
      if (sess.title && sess.title !== '新建会话') autoTitleDone.value[sid] = true;
      return;
    }
    titleGenerating.value[sid] = true;
    try {
      const firstUser = firstUserText;
      const t1 = instantTitleFromUserText(firstUser);
      // 后端同模型小请求优先（无工具/maxTokens 10000/20s 熔断，后端已持久化+置锁）；失败则本地精炼兜底
      let backendTitle = '';
      try {
        backendTitle = await requestTitleViaLlm(sid, 20000);
      } catch (e) {
        if ((e as { titleCode?: string })?.titleCode === 'TITLE_ALREADY_LOCKED') {
          // 后端已冻结（手动改名或跨刷新后已锁）：停手，不覆盖、不本地兜底
          if (sess.title && sess.title !== '新建会话') autoTitleDone.value[sid] = true;
          return;
        }
        backendTitle = '';
      }
      if (backendTitle) {
        const parsed = parseTitleResult(backendTitle, t1) || backendTitle;
        const item = sessions.value.find((s) => s.id === sid);
        if (item && !titleLocked.value[sid]) {
          item.title = parsed;
          titleLocked.value[sid] = true;
          autoTitleDone.value[sid] = true;
        }
        return;
      }
      const parsed = refineTitleLocal(assistantText, firstUser);
      if (!parsed) {
        return;
      }
      if (titleLocked.value[sid]) return;
      const still = sessions.value.find((s) => s.id === sid);
      if (!still) return;
      const ok = await renameSession(sid, parsed, { manual: false });
      if (ok) autoTitleDone.value[sid] = true;
    } catch (err) {
      console.warn('[workspaceStore] generateTitleAfterFirstRound skipped:', err);
    } finally {
      titleGenerating.value[sid] = false;
    }
  }
  // 会话就位后补跑一次精炼：仅当标题仍是首条即时形态时触发，门闩与后端冻结码天然去重
  function maybeRefineTitleAfterLoad(sessionId: string): void {
    try {
      const sid = (sessionId || '').trim();
      if (!sid || !activeWorkspaceId.value) return;
      if (titleLocked.value[sid] || autoTitleDone.value[sid] || titleGenerating.value[sid]) return;
      const sess = sessions.value.find((s) => s.id === sid);
      if (!sess) return;
      const sessRole = String((sess as unknown as { agentRole?: string }).agentRole || '').toLowerCase();
      if (sessRole === 'sub') return;
      if (sid !== activeSessionId.value) return;
      const title = String(sess.title || '').trim();
      const isBlank = !title || title === '新建会话' || title === '初始会话';
      if (!isBlank) {
        const memUsers = sessionMessages.value.filter((m) => m.role === 'user');
        if (memUsers.length === 0) {
          // 消息尚未落盘可见时仍给一次机会，计数以后端为准
        } else {
          const expected = instantTitleFromUserText(String(memUsers[0].content || ''));
          if (title !== expected) return;
        }
      }
      void generateTitleAfterFirstRound(sid).catch(() => undefined);
    } catch {
      /* 补跑永不阻断会话切换 */
    }
  }
  // 列表手动触发：显式覆盖已锁标题（force），允许反复跑、多轮也跑；空/sub/生成中由调用方前置拦截
  async function regenerateTitleManually(sessionId: string): Promise<boolean> {
    const sid = (sessionId || '').trim();
    if (!sid || !activeWorkspaceId.value) return false;
    if (titleGenerating.value[sid]) return false;
    const sess = sessions.value.find((s) => s.id === sid);
    if (!sess) return false;
    const sessRole = String((sess as unknown as { agentRole?: string }).agentRole || '').toLowerCase();
    if (sessRole === 'sub') return false;
    titleGenerating.value[sid] = true;
    try {
      const { firstUser, assistant, userCount } = await resolveFirstRoundTexts(sid);
      // DEBUG-TITLE: 临时诊断日志，定位“标题生成失败”后删除
      console.debug('[title-debug] resolve', { sid, userCount, firstLen: firstUser.trim().length, assistantLen: assistant.trim().length });
      if (userCount === 0 || !firstUser.trim()) {
        console.debug('[title-debug] early-return: 无可概括内容', { sid, userCount });
        return false;
      }
      const t1 = instantTitleFromUserText(firstUser);
      let backendTitle = '';
      try {
        backendTitle = await requestTitleViaLlm(sid, 20000, true);
      } catch (e) {
        // DEBUG-TITLE: 临时诊断日志，定位“标题生成失败”后删除
        console.debug('[title-debug] backendTitle failed', {
          sid,
          titleCode: (e as { titleCode?: string } | null)?.titleCode,
          message: e instanceof Error ? e.message : String(e),
        });
        backendTitle = '';
      }
      console.debug('[title-debug] backendTitle result', {
        sid,
        len: backendTitle.trim().length,
        preview: backendTitle.trim().slice(0, 20),
      });
      if (backendTitle) {
        const parsed = parseTitleResult(backendTitle, t1) || backendTitle;
        const item = sessions.value.find((s) => s.id === sid);
        if (!item) return false;
        // 手动显式覆盖：已锁也直接替换（后端 force 已跳锁，回写后保持锁定）
        item.title = parsed;
        titleLocked.value[sid] = true;
        autoTitleDone.value[sid] = true;
        return true;
      }
      const parsed = refineTitleLocal(assistant, firstUser);
      // DEBUG-TITLE: 临时诊断日志，定位“标题生成失败”后删除
      console.debug('[title-debug] local refine', { sid, parsed, titleLocked: !!titleLocked.value[sid] });
      if (!parsed) return false;
      if (titleLocked.value[sid]) {
        const item = sessions.value.find((s) => s.id === sid);
        if (!item) return false;
        item.title = parsed;
        autoTitleDone.value[sid] = true;
        return true;
      }
      const still = sessions.value.find((s) => s.id === sid);
      if (!still) return false;
      const ok = await renameSession(sid, parsed, { manual: false });
      if (ok) autoTitleDone.value[sid] = true;
      return ok;
    } catch (err) {
      console.warn('[workspaceStore] regenerateTitleManually skipped:', err);
      return false;
    } finally {
      titleGenerating.value[sid] = false;
    }
  }
  // need03-03 T2 后端标题通道：generateSessionTitle（同模型/temperature 0.2/maxTokens 10000/20s 熔断在后端内聚）
  async function requestTitleViaLlm(sessionId: string, timeoutMs: number, force = false): Promise<string> {
    const bridge = getHostBridge();
    const wid = activeWorkspaceId.value;
    if (!wid || !sessionId) throw new Error('no-workspace-session');
    const pId = configStore.activeProvider?.id || '';
    const mId = configStore.activeModel?.id || '';
    const call = bridge.generateSessionTitle(wid, sessionId, pId, mId, force);
    const timeout = new Promise<never>((_, reject) =>
      window.setTimeout(() => reject(new Error('title-timeout')), Math.max(1000, timeoutMs))
    );
    const res = await Promise.race([call, timeout]);
    if (!res || !res.ok) {
      const code = String((res as { code?: string } | null)?.code || '');
      throw Object.assign(new Error(String(res?.message || 'title-empty')), { titleCode: code });
    }
    if (!res.data || !String(res.data).trim()) throw new Error('title-empty');
    void truncateRoundText;
    return String(res.data);
  }

  // need05-03：/compact 统一入口（取 activeProvider/Model 传参，成功后重拉消息与预算），供命令与按钮共用
  async function compactSession(sessionId: string, keepLastGroups = 6): Promise<boolean> {
    if (!activeWorkspaceId.value || !sessionId) return false;
    if (isHistoryReadonly.value) return false;
    try {
      const bridge = getHostBridge();
      const pId = configStore.activeProvider?.id || '';
      const mId = configStore.activeModel?.id || '';
      const res = await bridge.compactSession(activeWorkspaceId.value, sessionId, pId, mId, keepLastGroups);
      if (res.ok) {
        if (sessionId === activeSessionId.value) {
          await loadSessionMessages(sessionId);
          await refreshBudget();
        }
        return true;
      }
      return false;
    } catch (err) {
      console.error('[workspaceStore] compactSession failed:', err);
      return false;
    }
  }

  async function exportSessionMarkdown(sessionId: string): Promise<string> {
    if (!activeWorkspaceId.value || !sessionId) return '';
    try {
      const bridge = getHostBridge();
      const res = await bridge.exportSessionMarkdown(activeWorkspaceId.value, sessionId);
      if (res.ok && res.data) {
        return res.data;
      }
    } catch (err) {
      console.error('[workspaceStore] exportSessionMarkdown failed:', err);
    }
    return '';
  }

  function applyUsagePatch(sid: string, usage: { inputTokens: number; outputTokens?: number; cachedInputTokens?: number | null }): void {
    if ((sid || '').trim() !== activeSessionId.value) return;
    const cur = budgetStatus.value;
    if (!cur) return;
    const input = Math.trunc(Number(usage?.inputTokens) || 0);
    if (input <= 0) return;
    if (String(cur.occupancyBaseline || 'exact') === 'compact-estimate') return;
    const cachedRaw = usage?.cachedInputTokens;
    const cached = cachedRaw === null || cachedRaw === undefined ? null : Math.max(0, Math.trunc(Number(cachedRaw) || 0));
    const total = Math.max(1, cur.totalContextTokens || 128000);
    const maxOut = cur.maxOutputTokens || 4096;
    const safety = cur.safetyBuffer || 1024;
    budgetStatus.value = {
      ...cur,
      usedInputTokens: input,
      lastPromptTokens: input,
      lastCachedTokens: cached,
      cacheHitRate: cached === null || input <= 0 ? null : Math.min(1, Math.max(0, cached / input)),
      usedInputIsEstimated: false,
      occupancyBaseline: 'exact',
      occupancyState: 'known',
      usagePercentage: Math.min(100, Math.round((input / total) * 1000) / 10),
      availableInputTokens: Math.max(0, total - maxOut - safety - input),
    };
  }

  async function refreshBudget(opts?: { signal?: AbortSignal | null; epoch?: number }): Promise<void> {
    if (!activeWorkspaceId.value || !activeSessionId.value) {
      budgetStatus.value = null;
      return;
    }
    // need22-P2 F3：快照 sid+epoch；旧 epoch 的预算回包不覆盖当前顶栏
    const sessionStore = useSessionStore();
    const epoch = opts?.epoch ?? sessionStore.epoch;
    const signal = opts?.signal ?? null;
    const sid = activeSessionId.value;
    try {
      const bridge = getHostBridge();
      const pId = configStore.activeProvider?.id || '';
      const mId = configStore.activeModel?.id || '';
      const res = await bridge.getContextBudget(pId, mId, activeWorkspaceId.value, sid);
      if (!sessionStore.isCurrent(epoch, signal) || sessionStore.activeSessionId !== sid) {
        sessionStore.discardStale(`refreshBudget(${sid.slice(-6)})`);
        return;
      }
      if (res.ok && res.data) {
        budgetStatus.value = res.data;
        budgetError.value = null;
      } else if (!res.ok) {
        budgetError.value = { code: res.code || 'BUDGET_FAILED', message: res.message || '预算刷新失败' };
        console.warn('[workspaceStore] refreshBudget failed:', budgetError.value);
      }
    } catch (err) {
      budgetError.value = { code: 'BUDGET_FAILED', message: err instanceof Error ? err.message : String(err) };
      console.warn('[workspaceStore] refreshBudget failed:', budgetError.value);
    }
  }

  // need07：重放截断（removeAnchor=true 为重放模式，切面以后端为准）
  async function truncateHistory(
    anchorMessageId: string,
    removeAnchor = true
  ): Promise<{ ok: boolean; replayText?: string; message?: string; code?: string }> {
    if (!activeWorkspaceId.value || !activeSessionId.value) return { ok: false, message: '会话不存在' };
    if (isHistoryReadonly.value) return { ok: false, message: '历史版本只读，不可截断，可分支', code: 'HISTORY_READONLY' };
    // need02-P1：先清本 session pending，避免旧乐观串入新历史。
    try {
      dropPendingUser(activeSessionId.value);
    } catch {
      /* ignore */
    }
    try {
      const bridge = getHostBridge();
      const res = await bridge.truncateSessionHistory(activeWorkspaceId.value, activeSessionId.value, anchorMessageId, removeAnchor);
      if (res.ok && res.data) {
        await loadSessionMessages(activeSessionId.value);
        await refreshBudget();
        return { ok: true, replayText: (res.data.replayText as string) || '' };
      }
      return { ok: false, message: res.message || '截断失败', code: res.code };
    } catch (err) {
      console.error('[workspaceStore] truncateHistory failed:', err);
      return { ok: false, message: String(err) };
    }
  }

  /** 分支：捕获源工作区与会话，草稿直接来自目标文本；返回结构化结果供 UI 提示。 */
  async function branchFromMessage(anchorMessageId: string, kind: 'user' | 'assistant', draftText?: string): Promise<HistoryBranchResult> {
    const sourceWorkspaceId = activeWorkspaceId.value;
    const sourceSessionId = activeSessionId.value;
    if (!sourceWorkspaceId || !sourceSessionId) {
      return { ok: false, code: 'SESSION_NOT_FOUND', message: '会话不存在' };
    }
    try {
      dropPendingUser(sourceSessionId);
    } catch {
      /* ignore */
    }
    try {
      const bridge = getHostBridge();
      const res = await bridge.branchSession(sourceWorkspaceId, sourceSessionId, anchorMessageId, kind === 'assistant');
      if (!res.ok || !res.data) {
        return {
          ok: false,
          code: res.code,
          message: res.message || historyErrorMessage(res.code),
          newSessionId: res.newSessionId ?? undefined,
          failedStage: res.failedStage ?? undefined,
        };
      }
      const created = res.data;
      sessions.value = [created, ...sessions.value.filter((s) => s.id !== created.id)];
      if (sourceWorkspaceId !== activeWorkspaceId.value || sourceSessionId !== activeSessionId.value) {
        return { ok: true, session: created, switched: false };
      }
      await selectSession(created.id);
      haloEventBus.emitLocal('chat:prefill-draft', {
        text: kind === 'user' ? String(draftText || '') : '',
        sessionId: created.id,
      });
      return { ok: true, session: created, switched: true };
    } catch (err) {
      console.error('[workspaceStore] branchFromMessage failed:', err);
      return {
        ok: false,
        code: 'BRANCH_ERROR',
        message: err instanceof Error ? err.message : String(err),
      };
    }
  }
  // 孤儿愈合：中断/杀进程残留的 tool_calls 补 synthetic cancelled tool。返回 true=落盘有变，调用方需重拉。
  async function healSessionHistory(opts?: { signal?: AbortSignal | null; epoch?: number }): Promise<boolean> {
    if (!activeWorkspaceId.value || !activeSessionId.value) return false;
    const sessionStore = useSessionStore();
    const epoch = opts?.epoch ?? sessionStore.epoch;
    const signal = opts?.signal ?? null;
    const sid = activeSessionId.value;
    try {
      const bridge = getHostBridge();
      const res = await bridge.healSessionHistory(activeWorkspaceId.value, sid);
      if (!sessionStore.isCurrent(epoch, signal) || sessionStore.activeSessionId !== sid) {
        sessionStore.discardStale(`healSessionHistory(${sid.slice(-6)})`);
        return false;
      }
      if (!res.ok || !res.data) return false;
      const d = res.data;
      if ((d.syntheticAppended || 0) > 0 || (d.orphanToolsRemoved || 0) > 0) {
        console.info(`[workspaceStore] history healed: synthetic=${d.syntheticAppended} removedOrphans=${d.orphanToolsRemoved}`);
        return true;
      }
      return false;
    } catch (err) {
      console.error('[workspaceStore] healSessionHistory failed:', err);
      return false;
    }
  }

  // 删除单条消息（后端先备份再删行，删后自动愈合；生成中拒绝）。
  async function deleteMessage(messageId: string): Promise<{ ok: boolean; message?: string; code?: string }> {
    if (!activeWorkspaceId.value || !activeSessionId.value) return { ok: false, message: '会话不存在' };
    if (!messageId) return { ok: false, message: '消息不存在' };
    try {
      dropPendingUser(activeSessionId.value);
    } catch {
      /* ignore */
    }
    try {
      const bridge = getHostBridge();
      const res = await bridge.deleteSessionMessage(activeWorkspaceId.value, activeSessionId.value, messageId);
      if (res.ok && res.data) {
        await loadSessionMessages(activeSessionId.value);
        await refreshBudget();
        return { ok: true };
      }
      return { ok: false, message: res.message || '删除失败', code: res.code };
    } catch (err) {
      console.error('[workspaceStore] deleteMessage failed:', err);
      return { ok: false, message: String(err) };
    }
  }

  async function initialize(): Promise<void> {
    await loadWorkspaces();
    if (activeWorkspaceId.value) {
      await loadSessions(activeWorkspaceId.value);
    }
    // 启动恢复完成后立即回写一次（存档指向已删ID时纠正为实际恢复位置）
    persistLastActive();
  }

  async function init(): Promise<void> {
    await initialize();
  }

  return {
    workspaces,
    activeWorkspaceId,
    activeWorkspace,
    sessions,
    activeSessionId,
    activeSession,
    isHistoryReadonly,
    sessionMessages,
    timelineSummaries,
    budgetStatus,
    budgetError,
    searchQuery,
    loading,
    loadingMessages,
    filteredSessions,
    pinnedSessions,
    unpinnedSessions,
    init,
    initialize,
    loadWorkspaces,
    selectWorkspace,
    addWorkspace,
    browseAndAddWorkspace,
    renameWorkspace,
    workspaceLabel,
    removeWorkspace,
    loadSessions,
    touchSessionTime,
    reconcileSessionTimes,
    selectSession,
    createSession,
    renameSession,
    togglePinSession,
    deleteSession,
    loadSessionMessages,
    loadTimelineSummary,
    getTurnBlockIds,
    getBlockContents,
    getToolResult,
    clearTimelineCaches,
    blockContentsCache,
    healSessionHistory,
    deleteMessage,
    truncateHistory,
    branchFromMessage,
    appendMessage,
    mergePersistedMessage,
    generateTitleAfterFirstRound,
    maybeRefineTitleAfterLoad,
    regenerateTitleManually,
    requestTitleViaLlm,
    titleLocked,
    autoTitleDone,
    titleGenerating,
    exportSessionMarkdown,
    compactSession,
    applyUsagePatch,
    refreshBudget,
  };
});
