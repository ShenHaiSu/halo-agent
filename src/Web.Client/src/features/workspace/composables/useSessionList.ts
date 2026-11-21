import { computed, onMounted, onUnmounted, ref } from 'vue';
import { storeToRefs } from 'pinia';
import { useDialog, useMessage } from 'naive-ui';
import { getHostBridge, type SessionMetadata } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAskUserStore } from '@/stores/askUserStore';

export function useSessionList() {
  const message = useMessage();
  const dialog = useDialog();
  const workspaceStore = useWorkspaceStore();
  const agentTurn = useAgentTurn();
  const askUserStore = useAskUserStore();
  const {
    workspaces,
    activeWorkspaceId,
    activeWorkspace,
    sessions,
    activeSessionId,
    searchQuery,
    pinnedSessions,
    unpinnedSessions,
    titleGenerating,
  } = storeToRefs(workspaceStore);
  const generatingIds = computed(() => Object.keys(titleGenerating.value).filter((k) => titleGenerating.value[k]));
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
  // —— 工作区别名内联编辑（与会话改名同构：editing + draft 两个 ref）——
  const editingWorkspaceId = ref<string | null>(null);
  const editingWorkspaceDraft = ref('');
  function startWorkspaceRename(ws: { id: string; alias?: string | null; name: string }): void {
    showWorkspacePopover.value = true; // 保持在切换器里编辑，不跳走
    editingWorkspaceId.value = ws.id;
    editingWorkspaceDraft.value = (ws.alias && ws.alias.trim()) || ws.name;
  }
  function setWorkspaceDraft(v: string): void {
    editingWorkspaceDraft.value = v;
  }
  function cancelWorkspaceRename(): void {
    editingWorkspaceId.value = null;
    editingWorkspaceDraft.value = '';
  }
  /** 提交：空串 = 清除别名（回退文件夹名）；与 name 相同也视为清除，避免存脏别名。 */
  async function handleSaveWorkspaceAlias(workspaceId: string): Promise<void> {
    if (editingWorkspaceId.value !== workspaceId) return;
    const raw = editingWorkspaceDraft.value.trim();
    const target = workspaceStore.workspaces.find((w) => w.id === workspaceId);
    const fallback = target?.name ?? '';
    const next = raw === fallback ? '' : raw;
    if (await workspaceStore.renameWorkspace(workspaceId, next)) {
      message.success(next ? '别名已更新' : '已恢复显示文件夹名');
    } else {
      message.error('别名保存失败');
    }
    cancelWorkspaceRename();
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
    if (titleGenerating.value[sessionId]) {
      message.warning('标题生成中，请稍候');
      return;
    }
    const newTitle = editingTitleDraft.value.trim();
    if (newTitle) await workspaceStore.renameSession(sessionId, newTitle);
    editingSessionId.value = null;
    editingTitleDraft.value = '';
  }
  function isBlankTitle(title: string): boolean {
    const t = (title || '').trim();
    return !t || t === '新建会话' || t === '初始会话';
  }
  async function handleRegenerateTitle(sess: SessionMetadata): Promise<void> {
    const sid = (sess.id || '').trim();
    if (!sid) return;
    if (titleGenerating.value[sid]) {
      message.info('标题生成中，请稍候');
      return;
    }
    const role = String((sess as unknown as { agentRole?: string }).agentRole || '').toLowerCase();
    if (role === 'sub') {
      message.warning('子会话标题由系统管理，不可重新生成');
      return;
    }
    if ((sess.messageCount ?? 0) <= 0) {
      message.warning('暂无可概括的对话内容');
      return;
    }
    const current = String(sess.title || '').trim();
    if (!isBlankTitle(current)) {
      const confirmed = await new Promise<boolean>((resolve) => {
        let done = false;
        const finish = (v: boolean): void => {
          if (done) return;
          done = true;
          resolve(v);
        };
        try {
          dialog.warning({
            title: '覆盖当前标题？',
            content: `将用新生成的标题覆盖「${current}」，继续吗？`,
            positiveText: '继续生成',
            negativeText: '取消',
            onPositiveClick: () => finish(true),
            onNegativeClick: () => finish(false),
            onClose: () => finish(false),
          });
        } catch {
          finish(true);
        }
      });
      if (!confirmed) return;
    }
    openMenuSessionId.value = null;
    const ok = await workspaceStore.regenerateTitleManually(sid);
    if (ok) message.success('标题已重新生成');
    else message.error('标题生成失败，请稍后重试');
  }
  async function handleSessionMenuSelect(key: string, sess: SessionMetadata): Promise<void> {
    if (isArmed(sess.id)) cancelDeleteConfirm();
    if (key === 'rename') {
      if (titleGenerating.value[sess.id]) {
        message.warning('标题生成中，请稍候');
        return;
      }
      openMenuSessionId.value = null;
      startInlineRename(sess);
    } else if (key === 'regenerate-title') {
      await handleRegenerateTitle(sess);
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
    generatingIds,
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
    editingWorkspaceId,
    editingWorkspaceDraft,
    startWorkspaceRename,
    setWorkspaceDraft,
    cancelWorkspaceRename,
    handleSaveWorkspaceAlias,
    handleCreateNewSession,
    handleSelectSession,
    startInlineRename,
    handleSaveRename,
    handleRegenerateTitle,
    handleSessionMenuSelect,
  };
}
export type SessionList = ReturnType<typeof useSessionList>;
