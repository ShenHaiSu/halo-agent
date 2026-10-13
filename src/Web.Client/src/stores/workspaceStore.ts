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

  async function renameSession(sessionId: string, newTitle: string): Promise<boolean> {
    if (!activeWorkspaceId.value || !newTitle.trim()) return false;
    try {
      const bridge = getHostBridge();
      const res = await bridge.updateSessionTitle(activeWorkspaceId.value, sessionId, newTitle.trim());
      if (res.ok && res.data) {
        const item = sessions.value.find((s) => s.id === sessionId);
        if (item) item.title = newTitle.trim();
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
    try {
      const bridge = getHostBridge();
      const res = await bridge.appendSessionMessage(activeWorkspaceId.value, activeSessionId.value, msg);
      if (res.ok && res.data) {
        sessionMessages.value.push(msg);
        const cur = activeSession.value;
        if (cur) {
          cur.messageCount = sessionMessages.value.length;
          cur.updatedAtUtc = new Date().toISOString();
          if (msg.role === 'user' && cur.title === '新建会话' && msg.content) {
            cur.title = msg.content.trim().slice(0, 24);
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
    exportSessionMarkdown,
    refreshBudget,
  };
});
