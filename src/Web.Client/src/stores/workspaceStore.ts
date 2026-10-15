  import { defineStore } from 'pinia';
  import { ref, computed } from 'vue';
  import {
    getHostBridge,
    type WorkspaceInfo,
    type SessionMetadata,
    type UnifiedChatMessage,
    type ContextBudgetStatusDto,
  } from '@/bridge/hostBridge';
  import { useConfigStore } from '@/stores/configStore';
  import { instantTitleFromUserText, parseTitleResult, refineTitleLocal, truncateRoundText } from '@/utils/sessionTitle';

export const useWorkspaceStore = defineStore('workspace', () => {
  const configStore = useConfigStore();

  const workspaces = ref<WorkspaceInfo[]>([]);
  const activeWorkspaceId = ref<string>('');

  const sessions = ref<SessionMetadata[]>([]);
  const activeSessionId = ref<string>('');

  const sessionMessages = ref<UnifiedChatMessage[]>([]);
  const budgetStatus = ref<ContextBudgetStatusDto | null>(null);

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
        // need03-01：旧数据缺时间则内存态回填（不回写磁盘，避免污染排序），保证时间行非空
        for (const s of res.data) {
          if (!s.createdAtUtc) (s as { createdAtUtc: string }).createdAtUtc = nowIso;
          if (!s.updatedAtUtc) (s as { updatedAtUtc: string }).updatedAtUtc = (s.createdAtUtc as string) || nowIso;
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
    if (activeSessionId.value === sessionId) return;
    activeSessionId.value = sessionId;
    await loadSessionMessages(sessionId);
    await refreshBudget();
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
        sessions.value = [newSess, ...sessions.value];
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
        if (item) item.title = newTitle.trim();
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
        if (item) item.isPinned = isPinned;
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

  async function loadSessionMessages(sessionId: string): Promise<void> {
    if (!activeWorkspaceId.value || !sessionId) {
      sessionMessages.value = [];
      return;
    }
    loadingMessages.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getSessionMessages(activeWorkspaceId.value, sessionId);
      if (res.ok && res.data) {
        sessionMessages.value = res.data;
      } else {
        sessionMessages.value = [];
      }
    } catch (err) {
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

  async function refreshBudget(): Promise<void> {
    if (!activeWorkspaceId.value || !activeSessionId.value) {
      budgetStatus.value = null;
      return;
    }
    try {
      const bridge = getHostBridge();
      const pId = configStore.activeProvider?.id || '';
      const mId = configStore.activeModel?.id || '';
      const res = await bridge.getContextBudget(pId, mId, activeWorkspaceId.value, activeSessionId.value);
      if (res.ok && res.data) {
        budgetStatus.value = res.data;
      }
    } catch (err) {
      console.error('[workspaceStore] refreshBudget failed:', err);
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
    selectSession,
    createSession,
    renameSession,
    togglePinSession,
    deleteSession,
    loadSessionMessages,
    appendMessage,
    generateTitleAfterFirstRound,
    titleLocked,
    autoTitleDone,
    titleGenerating,
    exportSessionMarkdown,
    refreshBudget,
  };
});
