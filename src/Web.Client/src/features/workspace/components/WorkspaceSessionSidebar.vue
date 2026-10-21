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
    :menu-id="s.openMenuSessionId"
    :now-tick="s.nowTick"
    :running-ids="s.runningIds"
    :ask-ids="s.askPendingIds"
    @update:popover="s.setPopover($event)"
    @update:search="s.setSearchQuery($event)"
    @update:draft="s.setDraft($event)"
    @select-workspace="s.handleSelectWorkspace($event)"
    @open-explorer="s.handleOpenWorkspaceExplorer($event)"
    @remove-workspace="s.handleRemoveWorkspace($event)"
    @add-workspace="s.handleAddNewWorkspace()"
    @create-session="s.handleCreateNewSession()"
    @select-session="s.handleSelectSession($event)"
    @delete-session="s.handleDeleteBtnClick($event)"
    @menu="(open, id) => s.onDropdownShowChange(open, id)"
    @menu-select="(key, sess) => s.handleSessionMenuSelect(key, sess)"
    @rename-start="s.startInlineRename($event)"
    @rename-save="s.handleSaveRename($event)"
    @open-active-folder="s.handleOpenActiveWorkspaceFolder()"
  />
</template>
<script setup lang="ts">
import { reactive } from 'vue';
import SidebarView from './SidebarView.vue';
import { useSessionList } from '../composables/useSessionList';

const s = reactive(useSessionList());
</script>
