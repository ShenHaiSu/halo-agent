<template>
  <aside class="h-full bg-sidebar border-r border-subtle flex flex-col p-2.5 gap-2.5 overflow-hidden select-none">
    <!-- 1. 顶部：当前工作区切换舱 (Workspace Switcher Popover) -->
    <div class="shrink-0">
      <n-popover
        v-model:show="showWorkspacePopover"
        trigger="click"
        placement="bottom-start"
        style="width: 360px; padding: 0"
      >
        <template #trigger>
          <div
            class="group p-2.5 rounded-md bg-surface hover:bg-elevated border border-subtle hover:border-accent/40 transition-colors cursor-pointer space-y-1.5"
            data-testid="workspace-switcher-trigger"
          >
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-1.5 min-w-0">
                <FolderGit2 class="w-4 h-4 text-accent shrink-0" />
                <span class="text-xs font-semibold truncate">{{ activeWorkspace?.name || '选择工作路径' }}</span>
              </div>
              <div class="flex items-center gap-1">
                <n-tag
                  v-if="activeWorkspace && !activeWorkspace.directoryExists"
                  size="tiny"
                  type="warning"
                  :bordered="false"
                >
                  路径失效
                </n-tag>
                <ChevronDown class="w-3.5 h-3.5 text-secondary group-hover:text-primary transition-transform" />
              </div>
            </div>
            <div
              class="text-[11px] text-secondary font-mono truncate"
              :title="activeWorkspace?.rootPath || ''"
            >
              {{ activeWorkspace?.rootPath || '未绑定工作路径' }}
            </div>
          </div>
        </template>

        <!-- 弹出面板内容 -->
        <div class="flex flex-col max-h-[460px]">
          <div class="p-3 border-b border-subtle flex items-center justify-between">
            <span class="text-xs font-semibold text-primary">
              已绑定工作路径 (共 {{ workspaces.length }} 个)
            </span>
          </div>

          <div class="p-2 overflow-y-auto flex-1 space-y-1 max-h-[320px]">
            <div
              v-for="ws in workspaces"
              :key="ws.id"
              class="group flex items-center justify-between p-2 rounded-md cursor-pointer transition-colors border"
              :class="
                ws.id === activeWorkspaceId
                  ? 'bg-elevated border-accent/40 text-primary'
                  : 'border-transparent hover:bg-surface text-secondary hover:text-primary'
              "
              @click="handleSelectWorkspace(ws.id)"
            >
              <div class="flex items-center gap-2.5 min-w-0 flex-1">
                <FolderOpen v-if="ws.id === activeWorkspaceId" class="w-4 h-4 text-accent shrink-0" />
                <Folder v-else class="w-4 h-4 shrink-0 text-secondary" />
                <div class="min-w-0 flex-1">
                  <div class="flex items-center gap-1.5">
                    <span class="text-xs font-medium truncate">{{ ws.name }}</span>
                    <n-tag
                      v-if="!ws.directoryExists"
                      size="tiny"
                      type="warning"
                      :bordered="false"
                      class="shrink-0"
                    >
                      <template #icon>
                        <AlertTriangle class="w-3 h-3" />
                      </template>
                      失效
                    </n-tag>
                  </div>
                  <div class="text-[10px] text-secondary font-mono truncate" :title="ws.rootPath">
                    {{ ws.rootPath }}
                  </div>
                </div>
              </div>

              <!-- 操作区 -->
              <div class="flex items-center gap-1 shrink-0 ml-2" @click.stop>
                <n-button
                  quaternary
                  circle
                  size="tiny"
                  title="在资源管理器中打开"
                  @click="handleOpenWorkspaceExplorer(ws.rootPath)"
                >
                  <template #icon>
                    <ExternalLink class="w-3.5 h-3.5" />
                  </template>
                </n-button>

                <n-popconfirm
                  v-if="workspaces.length > 1"
                  @positive-click="handleRemoveWorkspace(ws.id)"
                >
                  <template #trigger>
                    <n-button
                      quaternary
                      circle
                      size="tiny"
                      type="error"
                      title="从工作区列表移除"
                    >
                      <template #icon>
                        <Trash2 class="w-3.5 h-3.5" />
                      </template>
                    </n-button>
                  </template>
                  确认从列表移除该工作路径？（不会删除磁盘文件）
                </n-popconfirm>
              </div>
            </div>
          </div>

          <!-- 底部添加新工作区按钮 -->
          <div class="p-2.5 border-t border-subtle">
            <n-button
              dashed
              block
              type="primary"
              size="small"
              data-testid="add-workspace-btn"
              @click="handleAddNewWorkspace"
            >
              <template #icon>
                <FolderPlus class="w-4 h-4" />
              </template>
              添加新的工作路径 (绑定本地文件夹)
            </n-button>
          </div>
        </div>
      </n-popover>
    </div>

    <!-- 2. 中部：新建会话与搜索栏 -->
    <div class="shrink-0 space-y-2">
      <n-button
        type="primary"
        size="medium"
        block
        data-testid="new-session-btn"
        @click="handleCreateNewSession"
      >
        <template #icon>
          <Plus class="w-4 h-4" />
        </template>
        新建 Agent 会话
        <span class="ml-1 text-[11px] opacity-75 font-mono">(Ctrl+N)</span>
      </n-button>

      <n-input
        v-model:value="searchQuery"
        size="small"
        placeholder="搜索当前项目下的会话..."
        clearable
        data-testid="session-search-input"
      >
        <template #prefix>
          <Search class="w-3.5 h-3.5 text-secondary" />
        </template>
      </n-input>
    </div>

    <!-- 3. 下部：分组会话列表 (NScrollbar 独立滚动) -->
    <div class="flex-1 min-h-0 overflow-hidden">
      <n-scrollbar class="h-full pr-1">
        <div class="space-y-3">
          <!-- 置顶会话 -->
          <div v-if="pinnedSessions.length > 0" class="space-y-1">
            <div class="flex items-center gap-1 px-1.5 py-0.5 text-[10px] font-semibold text-secondary uppercase tracking-wider">
              <Pin class="w-3 h-3 text-accent" />
              <span>置顶会话 ({{ pinnedSessions.length }})</span>
            </div>

            <div
              v-for="sess in pinnedSessions"
              :key="sess.id"
              class="group relative flex items-center justify-between px-2.5 py-2 rounded-md cursor-pointer text-xs transition-colors border"
              :class="
                isArmed(sess.id)
                  ? 'border-red-500/60 bg-red-500/10 text-primary font-medium'
                  : sess.id === activeSessionId
                    ? 'bg-elevated border-accent/40 text-primary font-medium'
                    : 'border-transparent text-secondary hover:bg-surface hover:text-primary'
              "
              :data-testid="`session-item-${sess.id}`"
              @click="handleSelectSession(sess.id)"
            >
              <div class="flex items-center gap-2 min-w-0 flex-1">
                <div
                  class="w-2 h-2 rounded-full shrink-0"
                  :class="isSessionRunning(sess.id) ? 'bg-emerald-400 shadow-sm animate-pulse' : sess.id === activeSessionId ? 'bg-accent shadow-sm' : 'bg-secondary/40'"
                  :title="isSessionRunning(sess.id) ? '后台运行中' : ''"
                />
                
                <div class="min-w-0 flex-1">
                  <!-- 行内编辑模式 -->
                  <div v-if="editingSessionId === sess.id" class="my-0.5" @click.stop>
                    <n-input
                      ref="renameInputRef"
                      v-model:value="editingTitleDraft"
                      size="tiny"
                      autofocus
                      @keyup.enter="handleSaveRename(sess.id)"
                      @blur="handleSaveRename(sess.id)"
                    />
                  </div>
                  <div
                    v-else
                    class="truncate font-medium"
                    :title="sess.title"
                    @dblclick="startInlineRename(sess)"
                  >
                    {{ sess.title }}
                  </div>

                  <div class="flex items-center gap-1.5 text-[11px] text-secondary font-mono mt-0.5 truncate min-w-0 overflow-hidden" :title="sessionTimeTitle(sess)" data-testid="session-meta-line" :data-tick="nowTick">
                    <template v-if="hasNoTime(sess)"><span>时间未知</span></template>
                    <template v-else><span>{{ formatSessionTime(sess.createdAtUtc) }}</span><span class="opacity-50">·</span><span>活跃 {{ relativeLabel(sess.updatedAtUtc) }}</span></template>
                  </div>
                </div>
              </div>

              <!-- 悬停更多操作按钮 -->
              <!-- 悬停操作区：行外删除（二次确认） + 更多菜单 -->
              <div class="transition-opacity ml-1.5 shrink-0 flex items-center gap-1" :class="isArmed(sess.id) ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'" @click.stop>
                <n-button
                  quaternary
                  circle
                  size="tiny"
                  data-testid="session-delete-btn"
                  :data-armed="isArmed(sess.id) ? 'true' : 'false'"
                  :type="isArmed(sess.id) ? 'error' : 'default'"
                  :loading="deletingSessionId === sess.id"
                  :title="isArmed(sess.id) ? '再次点击确认删除（3s内有效）' : '删除会话'"
                  :class="isArmed(sess.id) ? '!bg-red-500/20 font-semibold' : ''"
                  @click="handleDeleteBtnClick(sess.id)"
                >
                  <template #icon>
                    <Trash2 class="w-3.5 h-3.5" :class="isArmed(sess.id) ? '' : 'text-error'" />
                  </template>
                </n-button>
                <n-dropdown
                  trigger="click"
                  :options="getSessionDropdownOptions(sess)"
                  :show="openMenuSessionId === sess.id"
                  @update:show="(v: boolean) => onDropdownShowChange(v, sess.id)"
                  @select="(key: string | number) => handleSessionMenuSelect(String(key), sess)"
                >
                  <n-button quaternary circle size="tiny" data-testid="session-more-btn" @click="openMenuSessionId === sess.id ? onDropdownShowChange(false, sess.id) : onDropdownShowChange(true, sess.id)">
                    <template #icon>
                      <MoreVertical class="w-3.5 h-3.5" />
                    </template>
                  </n-button>
                </n-dropdown>
              </div>
            </div>
          </div>

          <!-- 常规会话列表 -->
          <div class="space-y-1">
            <div class="flex items-center justify-between px-1.5 py-0.5 text-[10px] font-semibold text-secondary uppercase tracking-wider">
              <span>全部会话 ({{ unpinnedSessions.length }})</span>
            </div>

            <div v-if="unpinnedSessions.length === 0 && pinnedSessions.length === 0" class="p-4 text-center text-secondary text-xs">
              暂无会话，请点击上方创建
            </div>

            <div
              v-for="sess in unpinnedSessions"
              :key="sess.id"
              class="group relative flex items-center justify-between px-2.5 py-2 rounded-md cursor-pointer text-xs transition-colors border"
              :class="
                isArmed(sess.id)
                  ? 'border-red-500/60 bg-red-500/10 text-primary font-medium'
                  : sess.id === activeSessionId
                    ? 'bg-elevated border-accent/40 text-primary font-medium'
                    : 'border-transparent text-secondary hover:bg-surface hover:text-primary'
              "
              :data-testid="`session-item-${sess.id}`"
              @click="handleSelectSession(sess.id)"
            >
              <div class="flex items-center gap-2 min-w-0 flex-1">
                <div
                  class="w-2 h-2 rounded-full shrink-0"
                  :class="isSessionRunning(sess.id) ? 'bg-emerald-400 shadow-sm animate-pulse' : sess.id === activeSessionId ? 'bg-accent shadow-sm' : 'bg-secondary/40'"
                  :title="isSessionRunning(sess.id) ? '后台运行中' : ''"
                />

                <div class="min-w-0 flex-1">
                  <!-- 行内编辑模式 -->
                  <div v-if="editingSessionId === sess.id" class="my-0.5" @click.stop>
                    <n-input
                      ref="renameInputRef"
                      v-model:value="editingTitleDraft"
                      size="tiny"
                      autofocus
                      @keyup.enter="handleSaveRename(sess.id)"
                      @blur="handleSaveRename(sess.id)"
                    />
                  </div>
                  <div
                    v-else
                    class="truncate font-medium"
                    :title="sess.title"
                    @dblclick="startInlineRename(sess)"
                  >
                    {{ sess.title }}
                  </div>

                  <div class="flex items-center gap-1.5 text-[11px] text-secondary font-mono mt-0.5 truncate min-w-0 overflow-hidden" :title="sessionTimeTitle(sess)" data-testid="session-meta-line" :data-tick="nowTick">
                    <template v-if="hasNoTime(sess)"><span>时间未知</span></template>
                    <template v-else><span>{{ formatSessionTime(sess.createdAtUtc) }}</span><span class="opacity-50">·</span><span>活跃 {{ relativeLabel(sess.updatedAtUtc) }}</span></template>
                  </div>
                </div>
              </div>

              <!-- 悬停操作区：行外删除（二次确认） + 更多菜单 -->
              <div class="transition-opacity ml-1.5 shrink-0 flex items-center gap-1" :class="isArmed(sess.id) ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'" @click.stop>
                <n-button
                  quaternary
                  circle
                  size="tiny"
                  data-testid="session-delete-btn"
                  :data-armed="isArmed(sess.id) ? 'true' : 'false'"
                  :type="isArmed(sess.id) ? 'error' : 'default'"
                  :loading="deletingSessionId === sess.id"
                  :title="isArmed(sess.id) ? '再次点击确认删除（3s内有效）' : '删除会话'"
                  :class="isArmed(sess.id) ? '!bg-red-500/20 font-semibold' : ''"
                  @click="handleDeleteBtnClick(sess.id)"
                >
                  <template #icon>
                    <Trash2 class="w-3.5 h-3.5" :class="isArmed(sess.id) ? '' : 'text-error'" />
                  </template>
                </n-button>
                <n-dropdown
                  trigger="click"
                  :options="getSessionDropdownOptions(sess)"
                  :show="openMenuSessionId === sess.id"
                  @update:show="(v: boolean) => onDropdownShowChange(v, sess.id)"
                  @select="(key: string | number) => handleSessionMenuSelect(String(key), sess)"
                >
                  <n-button quaternary circle size="tiny" data-testid="session-more-btn" @click="openMenuSessionId === sess.id ? onDropdownShowChange(false, sess.id) : onDropdownShowChange(true, sess.id)">
                    <template #icon>
                      <MoreVertical class="w-3.5 h-3.5" />
                    </template>
                  </n-button>
                </n-dropdown>
              </div>
            </div>
          </div>
        </div>
      </n-scrollbar>
    </div>

    <!-- 4. 底部快捷工具条 -->
    <div class="shrink-0 pt-2 border-t border-subtle flex items-center justify-between text-xs text-secondary">
      <n-button
        quaternary
        size="tiny"
        class="!px-1.5 text-secondary hover:text-primary"
        @click="handleOpenActiveWorkspaceFolder"
      >
        <template #icon>
          <FolderOpen class="w-3.5 h-3.5" />
        </template>
        打开项目目录
      </n-button>

      <span class="font-mono text-[11px]">
        共 {{ sessions.length }} 个会话
      </span>
    </div>

  </aside>
</template>

<script setup lang="ts">
  import { ref, computed, onMounted, onUnmounted, h } from 'vue';
import { storeToRefs } from 'pinia';
import { useMessage } from 'naive-ui';
import {
  FolderGit2,
  Folder,
  FolderOpen,
  FolderPlus,
  ChevronDown,
  Search,
  Plus,
  Pin,
  Edit3,
  Hash,
  Trash2,
  ExternalLink,
  AlertTriangle,
  MoreVertical,
  Download,
} from 'lucide-vue-next';
import { getHostBridge, type SessionMetadata } from '@/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentStore } from '@/stores/agentStore';
import { formatSessionTime, formatRelativeTime, sessionTimeTitle, isBadTime } from '@/utils/timeFormat';

const message = useMessage();
const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();

const runningSessionIdSet = computed(() => new Set(agentStore.runningSessionIds));
function isSessionRunning(sessId: string): boolean {
  return runningSessionIdSet.value.has(sessId);
}


const {
  workspaces,
  activeWorkspaceId,
  activeWorkspace,
  sessions,
  activeSessionId,
  searchQuery,
  pinnedSessions,
  unpinnedSessions,
} = storeToRefs(workspaceStore);

const showWorkspacePopover = ref(false);
const editingSessionId = ref<string | null>(null);
const editingTitleDraft = ref('');
  const pendingDeleteSessionId = ref<string | null>(null);
  const deletingSessionId = ref<string | null>(null);
  let pendingDeleteTimer: number | null = null;
  // need03-02：受控菜单（保持展开的关键），null=关闭
  const openMenuSessionId = ref<string | null>(null);
  // need03-01：60s tick 驱动相对时间重算（模板经 relativeLabel 读取 nowTick）
  const nowTick = ref(Date.now());
  let tickTimer: number | null = null;

  function armDeleteConfirm(sessId: string): void {
    if (editingSessionId.value) return;
    if (deletingSessionId.value !== null) return;
    pendingDeleteSessionId.value = sessId;
    if (pendingDeleteTimer !== null) {
      clearTimeout(pendingDeleteTimer);
    }
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
    if (v) { openMenuSessionId.value = id; return; }
    if (openMenuSessionId.value === id) openMenuSessionId.value = null;
  }
  function isArmed(sessId: string): boolean {
    return pendingDeleteSessionId.value === sessId;
  }
  async function handleDeleteBtnClick(sessId: string): Promise<void> {
    if (deletingSessionId.value !== null) return;
    if (editingSessionId.value) return;
    if (!isArmed(sessId)) {
      armDeleteConfirm(sessId);
      return;
    }
    await confirmDeleteInline(sessId);
  }
  // need03-01：时间行展示兜底 + tick 依赖（空白行=用户视角“没展示”，非法时显示“时间未知”）
  // need04-01：坏时间统一判（空串/null/0001/无时区），新会话永不进此分支
  function hasNoTime(sess: SessionMetadata): boolean {
    return isBadTime((sess as unknown as { createdAtUtc: string }).createdAtUtc) && isBadTime((sess as unknown as { updatedAtUtc: string }).updatedAtUtc);
  }
  function relativeLabel(iso: string): string {
    void nowTick.value;
    return formatRelativeTime(iso);
  }

async function confirmDeleteInline(sessId: string): Promise<void> {
  if (deletingSessionId.value !== null) return;
  deletingSessionId.value = sessId;
  try {
    cancelDeleteConfirm();
    openMenuSessionId.value = null;
    const ok = await workspaceStore.deleteSession(sessId);
    if (ok) {
      message.success('会话已删除');
    }
  } finally {
    deletingSessionId.value = null;
  }
}

async function handleSelectWorkspace(workspaceId: string): Promise<void> {
  showWorkspacePopover.value = false;
  await workspaceStore.selectWorkspace(workspaceId);
}

async function handleOpenWorkspaceExplorer(rootPath: string): Promise<void> {
  const bridge = getHostBridge();
  await bridge.openPathInExplorer(rootPath);
}

async function handleOpenActiveWorkspaceFolder(): Promise<void> {
  if (activeWorkspace.value?.rootPath) {
    await handleOpenWorkspaceExplorer(activeWorkspace.value.rootPath);
  }
}

async function handleAddNewWorkspace(): Promise<void> {
  showWorkspacePopover.value = false;
  const ws = await workspaceStore.browseAndAddWorkspace();
  if (ws) {
    message.success(`成功绑定工作路径: ${ws.name}`);
  }
}

async function handleRemoveWorkspace(workspaceId: string): Promise<void> {
  const ok = await workspaceStore.removeWorkspace(workspaceId);
  if (ok) {
    message.success('已从列表移除该工作区');
  }
}

async function handleCreateNewSession(): Promise<void> {
  const sess = await workspaceStore.createSession();
  if (sess) {
    message.success(`已创建会话: ${sess.title}`);
  }
}

  async function handleSelectSession(sessionId: string): Promise<void> {
    cancelDeleteConfirm();
    openMenuSessionId.value = null;
    await workspaceStore.selectSession(sessionId);
  }

function startInlineRename(sess: SessionMetadata): void {
  editingSessionId.value = sess.id;
  editingTitleDraft.value = sess.title;
}

async function handleSaveRename(sessionId: string): Promise<void> {
  if (editingSessionId.value === sessionId) {
    const newTitle = editingTitleDraft.value.trim();
    if (newTitle) {
      await workspaceStore.renameSession(sessionId, newTitle);
    }
    editingSessionId.value = null;
    editingTitleDraft.value = '';
  }
}

  function getSessionDropdownOptions(sess: SessionMetadata) {
    return [
      {
        label: '重命名会话',
        key: 'rename',
        icon: () => h(Edit3, { class: 'w-4 h-4' }),
      },
      {
        label: sess.isPinned ? '取消置顶' : '置顶会话',
        key: 'pin',
        icon: () => h(Pin, { class: 'w-4 h-4' }),
      },
      {
        label: '复制会话 Hash',
        key: 'copy-hash',
        icon: () => h(Hash, { class: 'w-4 h-4' }),
      },
      {
        label: '导出为 Markdown',
        key: 'export-md',
        icon: () => h(Download, { class: 'w-4 h-4' }),
      },
    ];
  }
  async function handleSessionMenuSelect(key: string, sess: SessionMetadata): Promise<void> {
    if (isArmed(sess.id)) cancelDeleteConfirm();
    switch (key) {
      case 'rename':
        openMenuSessionId.value = null;
        startInlineRename(sess);
        break;
      case 'pin':
        await workspaceStore.togglePinSession(sess.id, !sess.isPinned);
        message.success(sess.isPinned ? '已取消置顶' : '已置顶会话');
        break;
      case 'copy-hash':
        try {
          await navigator.clipboard.writeText(sess.sessionHash);
          message.success(`已复制 Session Hash: ${sess.sessionHash}`);
        } catch {
          message.info(`Session Hash: ${sess.sessionHash}`);
        }
        break;
      case 'export-md': {
        const md = await workspaceStore.exportSessionMarkdown(sess.id);
        if (md) {
          try {
            await navigator.clipboard.writeText(md);
            message.success('已导出 Markdown 文本至剪贴板');
          } catch {
            message.success('Markdown 导出成功');
          }
        }
        break;
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
    handleCreateNewSession();
  }
}

  onMounted(() => {
    window.addEventListener('keydown', handleGlobalKeydown);
    if (tickTimer === null) tickTimer = window.setInterval(() => { nowTick.value = Date.now(); }, 60000);
  });
  onUnmounted(() => {
    window.removeEventListener('keydown', handleGlobalKeydown);
    if (pendingDeleteTimer !== null) {
      clearTimeout(pendingDeleteTimer);
      pendingDeleteTimer = null;
    }
    if (tickTimer !== null) { clearInterval(tickTimer); tickTimer = null; }
  });
</script>
