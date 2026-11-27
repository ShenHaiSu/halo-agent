<template>
  <SidebarView
    :workspaces="s.workspaces"
    :active-workspace="s.activeWorkspace"
    :active-workspace-id="s.activeWorkspaceId"
    :show-popover="s.showWorkspacePopover"
    :search-query="s.searchQuery"
    :pinned="s.pinnedSessions"
    :unpinned="s.unpinnedSessions"
    :total-count="s.sessions.length"
    :active-session-id="s.activeSessionId"
    :editing-id="s.editingSessionId"
    :draft="s.editingTitleDraft"
    :armed-id="s.pendingDeleteSessionId"
    :deleting-id="s.deletingSessionId"
    :can-delete-session="s.canDeleteSession"
    :can-remove-workspace="removal.canRemoveWorkspace"
    :menu-id="s.openMenuSessionId"
    :now-tick="s.nowTick"
    :running-ids="s.runningIds"
    :generating-ids="s.generatingIds"
    :ask-ids="s.askPendingIds"
    :editing-workspace-id="s.editingWorkspaceId"
    :workspace-draft="s.editingWorkspaceDraft"
    @update:popover="s.setPopover($event)"
    @update:search="s.setSearchQuery($event)"
    @update:draft="s.setDraft($event)"
    @select-workspace="s.handleSelectWorkspace($event)"
    @open-explorer="s.handleOpenWorkspaceExplorer($event)"
    @request-remove-workspace="removal.requestRemoval($event)"
    @add-workspace="s.handleAddNewWorkspace()"
    @rename-workspace-start="s.startWorkspaceRename($event)"
    @update:workspace-draft="s.setWorkspaceDraft($event)"
    @rename-workspace-save="s.handleSaveWorkspaceAlias($event)"
    @rename-workspace-cancel="s.cancelWorkspaceRename()"
    @create-session="s.handleCreateNewSession()"
    @select-session="s.handleSelectSession($event)"
    @delete-session="s.handleDeleteBtnClick($event)"
    @menu="(open, id) => s.onDropdownShowChange(open, id)"
    @menu-select="(key, sess) => s.handleSessionMenuSelect(key, sess)"
    @rename-start="s.startInlineRename($event)"
    @rename-save="s.handleSaveRename($event)"
    @open-active-folder="s.handleOpenActiveWorkspaceFolder()"
  />
  <WorkspaceRemovalDialog
    :open="removal.dialogOpen"
    :workspace="removal.targetWorkspace"
    :can-remove="removal.canRemoveWorkspace"
    :busy="removal.busy"
    @cancel="removal.cancelRemoval()"
    @confirm="(mode, text) => removal.confirmRemoval(mode, text)"
  />
</template>
<script setup lang="ts">
import { reactive } from 'vue';
import SidebarView from './SidebarView.vue';
import WorkspaceRemovalDialog from './WorkspaceRemovalDialog.vue';
import { useSessionList } from '../composables/useSessionList';
import { useWorkspaceRemoval } from '../composables/useWorkspaceRemoval';

const s = reactive(useSessionList());
const removal = reactive(useWorkspaceRemoval());
</script>
