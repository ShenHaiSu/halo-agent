import { computed, onMounted, onUnmounted, ref } from 'vue';
import { storeToRefs } from 'pinia';
import { useMessage } from 'naive-ui';
import { getHostBridge, type SessionMetadata } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAskUserStore } from '@/stores/askUserStore';

export function useSessionList() {
  const message = useMessage();
  const workspaceStore = useWorkspaceStore();
  const agentTurn = useAgentTurn();
  const askUserStore = useAskUserStore();
  const { workspaces, activeWorkspaceId, activeWorkspace, sessions, activeSessionId, searchQuery, pinnedSessions, unpinnedSessions } =
    storeToRefs(workspaceStore);
  const runningSessionIdSet = computed(() => new Set(agentTurn.runningSessionIds));
  const askPendingIds = computed(() => {
    try {
      return sessions.value.filter((s) => askUserStore.hasPending(s.id)).map((s) => s.id);
    } catch {
      return [] as string[];
    }
  });
  const runningIds = computed(() => [...runningSessionIdSet.value]);
  const showWorkspacePopover = ref(false);
  const editingSessionId = ref<string | null>(null);
  const editingTitleDraft = ref('');
  const pendingDeleteSessionId = ref<string | null>(null);
  const deletingSessionId = ref<string | null>(null);
  let pendingDeleteTimer: number | null = null;
  const openMenuSessionId = ref<string | null>(null);
  const nowTick = ref(Date.now());
  let tickTimer: number | null = null;
  function isSessionRunning(sessId: string): boolean {
    return runningSessionIdSet.value.has(sessId);
  }
  function hasAskPending(sessId: string): boolean {
    try {
      return askUserStore.hasPending(sessId);
    } catch {
      return false;
    }
  }
  function setSearchQuery(v: string): void {
    searchQuery.value = v;
  }
  function setPopover(v: boolean): void {
    showWorkspacePopover.value = v;
  }
  function setDraft(v: string): void {
    editingTitleDraft.value = v;
  }
  function armDeleteConfirm(sessId: string): void {
    if (editingSessionId.value || deletingSessionId.value !== null) return;
    pendingDeleteSessionId.value = sessId;
    if (pendingDeleteTimer !== null) clearTimeout(pendingDeleteTimer);
    pendingDeleteTimer = window.setTimeout(() => {
      pendingDeleteSessionId.value = null;
      pendingDeleteTimer = null;
    }, 3000);
  }
  function cancelDeleteConfirm(): void {
    pendingDeleteSessionId.value = null;
    if (pendingDeleteTimer !== null) {
      clearTimeout(pendingDeleteTimer);
      pendingDeleteTimer = null;
    }
  }
  function onDropdownShowChange(v: boolean, id: string): void {
    openMenuSessionId.value = v ? id : openMenuSessionId.value === id ? null : openMenuSessionId.value;
  }
  function isArmed(sessId: string): boolean {
    return pendingDeleteSessionId.value === sessId;
  }
  async function handleDeleteBtnClick(sessId: string): Promise<void> {
    if (deletingSessionId.value !== null || editingSessionId.value) return;
    if (!isArmed(sessId)) {
      armDeleteConfirm(sessId);
      return;
    }
    await confirmDeleteInline(sessId);
  }
  async function confirmDeleteInline(sessId: string): Promise<void> {
    if (deletingSessionId.value !== null) return;
    deletingSessionId.value = sessId;
    try {
      cancelDeleteConfirm();
      openMenuSessionId.value = null;
      if (await workspaceStore.deleteSession(sessId)) message.success('会话已删除');
    } finally {
      deletingSessionId.value = null;
    }
  }
  async function handleSelectWorkspace(workspaceId: string): Promise<void> {
    showWorkspacePopover.value = false;
    await workspaceStore.selectWorkspace(workspaceId);
  }
  async function handleOpenWorkspaceExplorer(rootPath: string): Promise<void> {
    await getHostBridge().openPathInExplorer(rootPath);
  }
  async function handleOpenActiveWorkspaceFolder(): Promise<void> {
    if (activeWorkspace.value?.rootPath) await handleOpenWorkspaceExplorer(activeWorkspace.value.rootPath);
  }
  async function handleAddNewWorkspace(): Promise<void> {
    showWorkspacePopover.value = false;
    const ws = await workspaceStore.browseAndAddWorkspace();
    if (ws) message.success(`成功绑定工作路径: ${ws.name}`);
  }
  async function handleRemoveWorkspace(workspaceId: string): Promise<void> {
    if (await workspaceStore.removeWorkspace(workspaceId)) message.success('已从列表移除该工作区');
  }
  async function handleCreateNewSession(): Promise<void> {
    const blank = workspaceStore.sessions.find((s) => (s.title || '').trim() === '新建会话' && (s.messageCount ?? 0) === 0 && !s.isPinned);
    if (blank) {
      await workspaceStore.selectSession(blank.id);
      message.info(`已选中空白会话: ${blank.title}`);
      return;
    }
    const sess = await workspaceStore.createSession();
    if (sess) message.success(`已创建会话: ${sess.title}`);
  }
  async function handleSelectSession(sessionId: string): Promise<void> {
    cancelDeleteConfirm();
    openMenuSessionId.value = null;
    await workspaceStore.selectSession(sessionId);
    if (hasAskPending(sessionId)) askUserStore.openModal(sessionId);
  }
  function startInlineRename(sess: SessionMetadata): void {
    editingSessionId.value = sess.id;
    editingTitleDraft.value = sess.title;
  }
  async function handleSaveRename(sessionId: string): Promise<void> {
    if (editingSessionId.value !== sessionId) return;
    const newTitle = editingTitleDraft.value.trim();
    if (newTitle) await workspaceStore.renameSession(sessionId, newTitle);
    editingSessionId.value = null;
    editingTitleDraft.value = '';
  }
  async function handleSessionMenuSelect(key: string, sess: SessionMetadata): Promise<void> {
    if (isArmed(sess.id)) cancelDeleteConfirm();
    if (key === 'rename') {
      openMenuSessionId.value = null;
      startInlineRename(sess);
    } else if (key === 'pin') {
      await workspaceStore.togglePinSession(sess.id, !sess.isPinned);
      message.success(sess.isPinned ? '已取消置顶' : '已置顶会话');
    } else if (key === 'copy-hash') {
      try {
        await navigator.clipboard.writeText(sess.sessionHash);
        message.success(`已复制 Session Hash: ${sess.sessionHash}`);
      } catch {
        message.info(`Session Hash: ${sess.sessionHash}`);
      }
    } else if (key === 'export-md') {
      const md = await workspaceStore.exportSessionMarkdown(sess.id);
      if (!md) return;
      try {
        await navigator.clipboard.writeText(md);
        message.success('已导出 Markdown 文本至剪贴板');
      } catch {
        message.success('Markdown 导出成功');
      }
    }
  }
  function handleGlobalKeydown(e: KeyboardEvent): void {
    if (e.key === 'Escape') {
      cancelDeleteConfirm();
      openMenuSessionId.value = null;
      return;
    }
    if (e.ctrlKey && (e.key === 'n' || e.key === 'N')) {
      e.preventDefault();
      void handleCreateNewSession();
    }
  }
  onMounted(() => {
    window.addEventListener('keydown', handleGlobalKeydown);
    if (tickTimer === null) tickTimer = window.setInterval(() => (nowTick.value = Date.now()), 60000);
  });
  onUnmounted(() => {
    window.removeEventListener('keydown', handleGlobalKeydown);
    if (pendingDeleteTimer !== null) {
      clearTimeout(pendingDeleteTimer);
      pendingDeleteTimer = null;
    }
    if (tickTimer !== null) {
      clearInterval(tickTimer);
      tickTimer = null;
    }
  });
  return {
    workspaces,
    activeWorkspaceId,
    activeWorkspace,
    sessions,
    activeSessionId,
    searchQuery,
    pinnedSessions,
    unpinnedSessions,
    runningIds,
    askPendingIds,
    showWorkspacePopover,
    editingSessionId,
    editingTitleDraft,
    pendingDeleteSessionId,
    deletingSessionId,
    openMenuSessionId,
    nowTick,
    isSessionRunning,
    hasAskPending,
    isArmed,
    setSearchQuery,
    setPopover,
    setDraft,
    onDropdownShowChange,
    handleDeleteBtnClick,
    handleSelectWorkspace,
    handleOpenWorkspaceExplorer,
    handleOpenActiveWorkspaceFolder,
    handleAddNewWorkspace,
    handleRemoveWorkspace,
    handleCreateNewSession,
    handleSelectSession,
    startInlineRename,
    handleSaveRename,
    handleSessionMenuSelect,
  };
}
export type SessionList = ReturnType<typeof useSessionList>;
