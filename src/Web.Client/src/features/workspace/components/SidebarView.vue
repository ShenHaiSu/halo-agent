<template>
  <aside class="h-full bg-sidebar border-r border-subtle flex flex-col p-2.5 gap-2.5 overflow-hidden select-none">
    <div class="shrink-0">
      <n-popover
        :show="showPopover"
        trigger="click"
        placement="bottom-start"
        style="width: 360px; padding: 0"
        @update:show="emit('update:popover', $event)"
      >
        <template #trigger>
          <div
            class="group p-2.5 rounded-md bg-surface hover:bg-elevated border border-subtle hover:border-accent/40 transition-colors cursor-pointer space-y-1.5"
            data-testid="workspace-switcher-trigger"
          >
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-1.5 min-w-0">
                <FolderGit2 class="w-4 h-4 text-accent shrink-0" />
                <span class="text-xs font-semibold truncate">{{ labelOf(activeWorkspace) || '选择工作路径' }}</span>
              </div>
              <div class="flex items-center gap-1">
                <n-tag v-if="activeWorkspace && !activeWorkspace.directoryExists" size="tiny" type="warning" :bordered="false">
                  路径失效
                </n-tag>
                <ChevronDown class="w-3.5 h-3.5 text-secondary group-hover:text-primary transition-transform" />
              </div>
            </div>
            <div class="text-[11px] text-secondary font-mono truncate" :title="activeWorkspace?.rootPath || ''">
              {{ activeWorkspace?.rootPath || '未绑定工作路径' }}
            </div>
          </div>
        </template>
        <div class="flex flex-col max-h-[460px]">
          <div class="p-3 border-b border-subtle flex items-center justify-between">
            <span class="text-xs font-semibold text-primary"> 已绑定工作路径 (共 {{ workspaces.length }} 个) </span>
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
              @click="emit('select-workspace', ws.id)"
            >
              <div class="flex items-center gap-2.5 min-w-0 flex-1">
                <FolderOpen v-if="ws.id === activeWorkspaceId" class="w-4 h-4 text-accent shrink-0" />
                <Folder v-else class="w-4 h-4 shrink-0 text-secondary" />
                <div class="min-w-0 flex-1">
                  <div class="flex items-center gap-1.5">
                    <div v-if="editingWorkspaceId === ws.id" class="min-w-0 flex-1" @click.stop>
                      <n-input
                        :value="workspaceDraft"
                        size="tiny"
                        autofocus
                        placeholder="输入别名，留空恢复文件夹名"
                        :data-testid="`workspace-alias-input-${ws.id}`"
                        @update:value="emit('update:workspace-draft', $event)"
                        @keyup.enter="emit('rename-workspace-save', ws.id)"
                        @keyup.esc="emit('rename-workspace-cancel')"
                        @blur="emit('rename-workspace-save', ws.id)"
                      />
                    </div>
                    <template v-else>
                      <span
                        class="text-xs font-medium truncate"
                        :title="aliasTitleOf(ws)"
                        :data-testid="`workspace-name-${ws.id}`"
                        @dblclick.stop="emit('rename-workspace-start', ws)"
                        >{{ labelOf(ws) }}</span
                      >
                      <span v-if="hasAlias(ws)" class="text-[10px] text-secondary/70 truncate shrink-0">({{ ws.name }})</span>
                      <n-tag v-if="!ws.directoryExists" size="tiny" type="warning" :bordered="false" class="shrink-0">
                        <template #icon>
                          <AlertTriangle class="w-3 h-3" />
                        </template>
                        失效
                      </n-tag>
                    </template>
                  </div>
                  <div class="text-[10px] text-secondary font-mono truncate" :title="ws.rootPath">
                    {{ ws.rootPath }}
                  </div>
                </div>
              </div>
              <div class="flex items-center gap-1 shrink-0 ml-2" @click.stop>
                <n-button
                  v-if="editingWorkspaceId !== ws.id"
                  quaternary
                  circle
                  size="tiny"
                  title="重命名别名（双击名称亦可）"
                  :data-testid="`workspace-rename-btn-${ws.id}`"
                  @click="emit('rename-workspace-start', ws)"
                >
                  <template #icon>
                    <Pencil class="w-3.5 h-3.5" />
                  </template>
                </n-button>
                <n-button quaternary circle size="tiny" title="在资源管理器中打开" @click="emit('open-explorer', ws.rootPath)">
                  <template #icon>
                    <ExternalLink class="w-3.5 h-3.5" />
                  </template>
                </n-button>
                <n-popconfirm v-if="workspaces.length > 1" @positive-click="emit('remove-workspace', ws.id)">
                  <template #trigger>
                    <n-button quaternary circle size="tiny" type="error" title="从工作区列表移除">
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
          <div class="p-2.5 border-t border-subtle">
            <n-button dashed block type="primary" size="small" data-testid="add-workspace-btn" @click="emit('add-workspace')">
              <template #icon>
                <FolderPlus class="w-4 h-4" />
              </template>
              添加新的工作路径 (绑定本地文件夹)
            </n-button>
            <p class="mt-1.5 text-[10px] text-secondary/70 leading-snug">别名仅改变界面显示，不影响磁盘路径与提示词。</p>
          </div>
        </div>
      </n-popover>
    </div>
    <div class="shrink-0 space-y-2">
      <n-button type="primary" size="medium" block data-testid="new-session-btn" @click="emit('create-session')">
        <template #icon>
          <Plus class="w-4 h-4" />
        </template>
        新建 Agent 会话
        <span class="ml-1 text-[11px] opacity-75 font-mono">(Ctrl+N)</span>
      </n-button>
      <n-input
        :value="searchQuery"
        size="small"
        placeholder="搜索当前项目下的会话..."
        clearable
        data-testid="session-search-input"
        @update:value="emit('update:search', $event)"
      >
        <template #prefix>
          <Search class="w-3.5 h-3.5 text-secondary" />
        </template>
      </n-input>
    </div>
    <div class="flex-1 min-h-0 overflow-hidden">
      <n-scrollbar class="h-full pr-1">
        <div class="space-y-3">
          <div v-if="pinned.length > 0" class="space-y-1">
            <div class="flex items-center gap-1 px-1.5 py-0.5 text-[10px] font-semibold text-secondary uppercase tracking-wider">
              <Pin class="w-3 h-3 text-accent" />
              <span>置顶会话 ({{ pinned.length }})</span>
            </div>
            <SessionListItem
              v-for="sess in pinned"
              :key="sess.id"
              :sess="sess"
              :active="sess.id === activeSessionId"
              :armed="armedId === sess.id"
              :running="runningIds.includes(sess.id)"
              :editing="editingId === sess.id"
              :draft="draft"
              :ask-pending="askIds.includes(sess.id)"
              :deleting="deletingId === sess.id"
              :menu-open="menuId === sess.id"
              :now-tick="nowTick"
              @select="emit('select-session', $event)"
              @delete="emit('delete-session', $event)"
              @menu="emit('menu', $event, sess.id)"
              @menu-select="emit('menu-select', $event, sess)"
              @rename-start="emit('rename-start', $event)"
              @rename-save="emit('rename-save', $event)"
              @update:draft="emit('update:draft', $event)"
            />
          </div>
          <div class="space-y-1">
            <div class="flex items-center justify-between px-1.5 py-0.5 text-[10px] font-semibold text-secondary uppercase tracking-wider">
              <span>全部会话 ({{ unpinned.length }})</span>
            </div>
            <div v-if="unpinned.length === 0 && pinned.length === 0" class="p-4 text-center text-secondary text-xs">
              暂无会话，请点击上方创建
            </div>
            <SessionListItem
              v-for="sess in unpinned"
              :key="sess.id"
              :sess="sess"
              :active="sess.id === activeSessionId"
              :armed="armedId === sess.id"
              :running="runningIds.includes(sess.id)"
              :editing="editingId === sess.id"
              :draft="draft"
              :ask-pending="askIds.includes(sess.id)"
              :deleting="deletingId === sess.id"
              :menu-open="menuId === sess.id"
              :now-tick="nowTick"
              @select="emit('select-session', $event)"
              @delete="emit('delete-session', $event)"
              @menu="emit('menu', $event, sess.id)"
              @menu-select="emit('menu-select', $event, sess)"
              @rename-start="emit('rename-start', $event)"
              @rename-save="emit('rename-save', $event)"
              @update:draft="emit('update:draft', $event)"
            />
          </div>
        </div>
      </n-scrollbar>
    </div>
    <div class="shrink-0 pt-2 border-t border-subtle flex items-center justify-between text-xs text-secondary">
      <n-button quaternary size="tiny" class="!px-1.5 text-secondary hover:text-primary" @click="emit('open-active-folder')">
        <template #icon>
          <FolderOpen class="w-3.5 h-3.5" />
        </template>
        打开项目目录
      </n-button>
      <span class="font-mono text-[11px]"> 共 {{ totalCount }} 个会话 </span>
    </div>
  </aside>
</template>
<script setup lang="ts">
import {
  FolderGit2,
  Folder,
  FolderOpen,
  FolderPlus,
  ChevronDown,
  Search,
  Plus,
  Trash2,
  ExternalLink,
  AlertTriangle,
  Pin,
  Pencil,
} from 'lucide-vue-next';
import type { SessionMetadata, WorkspaceInfo } from '@shared/bridge/hostBridge';
import SessionListItem from './SessionListItem.vue';

/** 工作区展示名：alias 优先（纯视觉），空则回退 name。 */
function labelOf(ws: WorkspaceInfo | null | undefined): string {
  if (!ws) return '';
  return (ws.alias && ws.alias.trim()) || ws.name || '';
}
/** 是否设置了与文件夹名不同的别名（用于展示"(文件夹名)"副标）。 */
function hasAlias(ws: WorkspaceInfo): boolean {
  return !!ws.alias && ws.alias.trim() !== ws.name;
}
/** 悬浮提示：有别名时给"别名 + 真实路径"，否则给路径。 */
function aliasTitleOf(ws: WorkspaceInfo): string {
  return hasAlias(ws) ? `${ws.alias}\n${ws.rootPath}` : ws.rootPath;
}

defineProps<{
  workspaces: WorkspaceInfo[];
  activeWorkspace: WorkspaceInfo | null;
  activeWorkspaceId: string;
  showPopover: boolean;
  searchQuery: string;
  pinned: SessionMetadata[];
  unpinned: SessionMetadata[];
  totalCount: number;
  activeSessionId: string;
  editingId: string | null;
  draft: string;
  armedId: string | null;
  deletingId: string | null;
  menuId: string | null;
  nowTick: number;
  runningIds: string[];
  askIds: string[];
  editingWorkspaceId: string | null;
  workspaceDraft: string;
}>();
const emit = defineEmits<{
  'update:popover': [v: boolean];
  'update:search': [v: string];
  'update:draft': [v: string];
  'select-workspace': [id: string];
  'open-explorer': [path: string];
  'remove-workspace': [id: string];
  'add-workspace': [];
  'rename-workspace-start': [ws: WorkspaceInfo];
  'update:workspace-draft': [v: string];
  'rename-workspace-save': [id: string];
  'rename-workspace-cancel': [];
  'create-session': [];
  'select-session': [id: string];
  'delete-session': [id: string];
  menu: [open: boolean, id: string];
  'menu-select': [key: string, sess: SessionMetadata];
  'rename-start': [sess: SessionMetadata];
  'rename-save': [id: string];
  'open-active-folder': [];
}>();
</script>
