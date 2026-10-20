  import { defineStore } from 'pinia';
  import { ref, computed, watch } from 'vue';
  import {
    getHostBridge,
    haloEventBus,
    type WorkspaceInfo,
    type SessionMetadata,
    type UnifiedChatMessage,
    type ContextBudgetStatusDto,
  } from '@/bridge/hostBridge';
  import { useConfigStore } from '@/stores/configStore';
  import { useSessionStore } from '@/stores/sessionStore';
  import { isBadTime } from '@/utils/timeFormat';
import { instantTitleFromUserText, parseTitleResult, refineTitleLocal, truncateRoundText } from '@/utils/sessionTitle';

export const useWorkspaceStore = defineStore('workspace', () => {
  const configStore = useConfigStore();

  const workspaces = ref<WorkspaceInfo[]>([]);
  const activeWorkspaceId = ref<string>('');

  const sessions = ref<SessionMetadata[]>([]);
  const activeSessionId = ref<string>('');
  // need22-P2 F1：activeSessionId 唯一真相=sessionStore；本 ref 为兼容镜像，任何写入同步过去。
  const sessionStore = useSessionStore();
  watch(activeSessionId, (sid) => { sessionStore.syncActive(sid || ''); }, { flush: 'sync' });
  const sessionMessages = ref<UnifiedChatMessage[]>([]);
  const budgetStatus = ref<ContextBudgetStatusDto | null>(null);
  // need22-P3 G6：budget 失败显式角标（顶栏⚠），统一 {code,message}
  const budgetError = ref<{ code: string; message: string } | null>(null);

  const searchQuery = ref<string>('');
  const loading = ref<boolean>(false);
  const loadingMessages = ref<boolean>(false);
  // need03-03：自动标题冻结位（手动改名最高优，此后永不覆盖）+ 首轮已生成位 + 生成中位
  const titleLocked = ref<Record<string, boolean>>({});
  const autoTitleDone = ref<Record<string, boolean>>({});
  const titleGenerating = ref<Record<string, boolean>>({});

  const activeWorkspace = computed<WorkspaceInfo | null>(() => {
    return (
      workspaces.value.find((w) => w.id === activeWorkspaceId.value) ||
      workspaces.value[0] ||
      null
    );
  });

  const activeSession = computed<SessionMetadata | null>(() => {
    return (
      sessions.value.find((s) => s.id === activeSessionId.value) ||
      sessions.value[0] ||
      null
    );
  });

  const filteredSessions = computed<SessionMetadata[]>(() => {
    const q = searchQuery.value.trim().toLowerCase();
    if (!q) return sessions.value;
    return sessions.value.filter(
      (s) =>
        s.title.toLowerCase().includes(q) ||
        s.sessionHash.toLowerCase().includes(q) ||
        s.id.toLowerCase().includes(q)
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
        if (
          !activeWorkspaceId.value ||
          !workspaces.value.some((w) => w.id === activeWorkspaceId.value)
        ) {
          if (workspaces.value.length > 0) {
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
          if (activeWorkspaceId.value) {
            await loadSessions(activeWorkspaceId.value);
          } else {
            sessions.value = [];
            activeSessionId.value = '';
            sessionMessages.value = [];
            budgetStatus.value = null;
          }
        }
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] removeWorkspace failed:', err);
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
    } catch { /* 对账失败只打日志，不阻断 */ }
  }

  async function loadSessions(workspaceId: string): Promise<void> {
    if (!workspaceId) {
      sessions.value = [];
      activeSessionId.value = '';
      sessionMessages.value = [];
      budgetStatus.value = null;
      return;
    }

    try {
      const bridge = getHostBridge();
      const res = await bridge.getSessions(workspaceId);
      if (res.ok && res.data) {
        const nowIso = new Date().toISOString();
        // need04-01：坏时间统一回填（空串/null/0001/Invalid/年<2000），内存态不回写磁盘
        for (const s of res.data) {
          if (isBadTime((s as unknown as { createdAtUtc: string }).createdAtUtc)) (s as unknown as { createdAtUtc: string }).createdAtUtc = nowIso;
          if (isBadTime((s as unknown as { updatedAtUtc: string }).updatedAtUtc)) (s as unknown as { updatedAtUtc: string }).updatedAtUtc = (s.createdAtUtc as string) || nowIso;
          const c = new Date((s as unknown as { createdAtUtc: string }).createdAtUtc).getTime();
          const u = new Date((s as unknown as { updatedAtUtc: string }).updatedAtUtc).getTime();
          if (!isNaN(c) && !isNaN(u) && u < c) (s as unknown as { updatedAtUtc: string }).updatedAtUtc = (s as unknown as { createdAtUtc: string }).createdAtUtc;
        }
        sessions.value = res.data;
        if (
          !activeSessionId.value ||
          !sessions.value.some((s) => s.id === activeSessionId.value)
        ) {
          if (sessions.value.length > 0) {
            activeSessionId.value = sessions.value[0].id;
          } else {
            activeSessionId.value = '';
          }
        }

         if (activeSessionId.value) {
           await loadSessionMessages(activeSessionId.value);
           // 启动/工作区进入：同样做一次孤儿愈合，脏了才重拉
           if (await healSessionHistory()) await loadSessionMessages(activeSessionId.value);
           await refreshBudget();
         } else {
          sessionMessages.value = [];
          budgetStatus.value = null;
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
   }

  async function createSession(
    title?: string,
    providerId?: string,
    modelId?: string
  ): Promise<SessionMetadata | null> {
    if (!activeWorkspaceId.value) return null;

    try {
      const bridge = getHostBridge();
      const pId = providerId || configStore.activeProvider?.id;
      const mId = modelId || configStore.activeModel?.id;
      const res = await bridge.createSession(activeWorkspaceId.value, title, pId, mId);
      if (res.ok && res.data) {
        const newSess = res.data;
        if (isBadTime((newSess as unknown as { createdAtUtc: string }).createdAtUtc)) (newSess as unknown as { createdAtUtc: string }).createdAtUtc = new Date().toISOString();
        if (isBadTime((newSess as unknown as { updatedAtUtc: string }).updatedAtUtc)) (newSess as unknown as { updatedAtUtc: string }).updatedAtUtc = (newSess as unknown as { createdAtUtc: string }).createdAtUtc;
        sessions.value = [newSess, ...sessions.value];
        touchSessionTime(newSess.id, (newSess as unknown as { createdAtUtc: string }).createdAtUtc);
        activeSessionId.value = newSess.id;
        sessionMessages.value = [];
        await refreshBudget();
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
        if (item) { item.title = newTitle.trim(); touchSessionTime(item.id); }
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
        if (item) { item.isPinned = isPinned; touchSessionTime(item.id); }
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
            budgetStatus.value = null;
          }
        }
        return true;
      }
    } catch (err) {
      console.error('[workspaceStore] deleteSession failed:', err);
    }
    return false;
  }

  async function loadSessionMessages(sessionId: string, opts?: { signal?: AbortSignal | null; epoch?: number }): Promise<void> {
    if (!activeWorkspaceId.value || !sessionId) {
      sessionMessages.value = [];
      return;
    }
    // need22-P2 F3：无显式 epoch 时按进入时 epoch 快照；旧 epoch 响应丢弃不覆盖视图
    const sessionStore = useSessionStore();
    const epoch = opts?.epoch ?? sessionStore.epoch;
    const signal = opts?.signal ?? null;
    const sid = (sessionId || '').trim();
    loadingMessages.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getSessionMessages(activeWorkspaceId.value, sid);
      if (!sessionStore.isCurrent(epoch, signal) || sessionStore.activeSessionId !== sid) {
        sessionStore.discardStale(`loadSessionMessages(${sid.slice(-6)})`);
        return;
      }
      if (res.ok && res.data) {
        sessionMessages.value = res.data;
      } else {
        sessionMessages.value = [];
      }
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
          // need03-03 T1：首条 user 发出即 5 字标题（第一句话前 5 字），并持久化
          if (msg.role === 'user' && !titleLocked.value[sid] && !autoTitleDone.value[sid] && (cur.title === '新建会话' || !cur.title?.trim()) && msg.content) {
            const t1 = instantTitleFromUserText(String(msg.content));
            cur.title = t1;
            try { await bridge.updateSessionTitle(wid, sid, t1); } catch { /* 静默：T1 内存已更新 */ }
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
  // need03-03 T2：首轮响应结束后精炼一次（同模型小请求优先，失败则本地精炼兜底；手动锁/已生成/生成中直接跳过）
  async function generateTitleAfterFirstRound(sessionId: string): Promise<void> {
    const sid = (sessionId || '').trim();
    if (!sid || titleLocked.value[sid] || autoTitleDone.value[sid] || titleGenerating.value[sid]) return;
    const sess = sessions.value.find((s) => s.id === sid);
    if (!sess) return;
    // 仅首轮：当前内存消息中 user 条数须为 1（后台会话消息未加载时跳过，避免误覆）
    if (sid !== activeSessionId.value) return;
    const users = sessionMessages.value.filter((m) => m.role === 'user');
    if (users.length !== 1) {
      // 非首轮或空：若标题仍是新建会话则不处理；否则标记完成以冻结
      if (sess.title && sess.title !== '新建会话') autoTitleDone.value[sid] = true;
      return;
    }
    titleGenerating.value[sid] = true;
    try {
      const firstUser = String(users[0].content || '');
      const firstAssistant = [...sessionMessages.value].reverse().find((m) => m.role === 'assistant');
      const assistantText = String(firstAssistant?.content || '');
      const t1 = instantTitleFromUserText(firstUser);
      // 后端同模型小请求优先（无工具/maxTokens 50/20s 熔断，后端已持久化+置锁）；失败则本地精炼兜底
      let backendTitle = '';
      try {
        backendTitle = await requestTitleViaLlm(sid, 20000);
      } catch {
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
      let parsed = refineTitleLocal(assistantText, firstUser);
      if (!parsed) { return; }
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
  // need03-03 T2 后端标题通道：generateSessionTitle（同模型/temperature 0.2/maxTokens 50/20s 熔断在后端内聚）
  async function requestTitleViaLlm(sessionId: string, timeoutMs: number): Promise<string> {
    const bridge = getHostBridge();
    const wid = activeWorkspaceId.value;
    if (!wid || !sessionId) throw new Error('no-workspace-session');
    const pId = configStore.activeProvider?.id || '';
    const mId = configStore.activeModel?.id || '';
    const call = bridge.generateSessionTitle(wid, sessionId, pId, mId);
    const timeout = new Promise<never>((_, reject) => window.setTimeout(() => reject(new Error('title-timeout')), Math.max(1000, timeoutMs)));
    const res = await Promise.race([call, timeout]);
    if (!res || !res.ok || !res.data || !String(res.data).trim()) throw new Error(String((res && res.message) || 'title-empty'));
    void truncateRoundText;
    return String(res.data);
  }

  // need05-03：/compact 统一入口（取 activeProvider/Model 传参，成功后重拉消息与预算），供命令与按钮共用
  async function compactSession(sessionId: string, keepLastGroups = 6): Promise<boolean> {
    if (!activeWorkspaceId.value || !sessionId) return false;
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
  async function truncateHistory(anchorMessageId: string, removeAnchor = true): Promise<{ ok: boolean; replayText?: string; message?: string; code?: string }> {
    if (!activeWorkspaceId.value || !activeSessionId.value) return { ok: false, message: '会话不存在' };
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

  // need07：分支（user→排除锚点+草稿预填原文；assistant→含整轮+草稿空）
  async function branchFromMessage(anchorMessageId: string, kind: 'user' | 'assistant'): Promise<boolean> {
    if (!activeWorkspaceId.value || !activeSessionId.value) return false;
    const includeAnchor = kind === 'assistant';
    const anchor = sessionMessages.value.find((m) => String(m.id) === String(anchorMessageId));
    const draftText = kind === 'user' ? String(anchor?.content || '') : '';
    try {
      const bridge = getHostBridge();
      const res = await bridge.branchSession(activeWorkspaceId.value, activeSessionId.value, anchorMessageId, includeAnchor);
      if (!res.ok || !res.data) return false;
      const created = res.data;
      sessions.value = [created, ...sessions.value.filter((s) => s.id !== created.id)];
      await selectSession(created.id);
      haloEventBus.emitLocal('chat:prefill-draft', { text: draftText, sessionId: created.id });
      return true;
    } catch (err) {
      console.error('[workspaceStore] branchFromMessage failed:', err);
      return false;
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
    sessionMessages,
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
     healSessionHistory,
     deleteMessage,
    truncateHistory,
    branchFromMessage,
    appendMessage,
    generateTitleAfterFirstRound,
    titleLocked,
    autoTitleDone,
    titleGenerating,
    exportSessionMarkdown,
    compactSession,
    refreshBudget,
  };
});
