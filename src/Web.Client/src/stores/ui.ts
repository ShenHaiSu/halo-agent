// src/Web.Client/src/stores/ui.ts
// P5 扩展：settings / commandPalette / collapsedTools / detailTab 等。
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { useToast } from '@/composables/useToast';

export type ModalKey = 'settings' | 'about' | 'permissions' | null;

export const useUiStore = defineStore('ui', () => {
  const sidebarVisible = ref(true);
  const detailVisible = ref(false);
  const activeModal = ref<ModalKey>(null);
  const toast = useToast();

  const sidebarCollapsed = ref(false);
  const detailCollapsed = ref(false);
  const sidebarWidth = ref(280);
  const detailWidth = ref(320);
  const detailTab = ref('usage');
  const collapsedTools = ref(new Set<string>());
  const settingsOpen = ref(false);
  const settingsTab = ref('general');
  const commandPaletteOpen = ref(false);
  const shortcutHelpOpen = ref(false);
  const focusMode = ref(false);
  const themeMode = ref('light');

  function toggleSidebar(): void {
    sidebarVisible.value = !sidebarVisible.value;
    sidebarCollapsed.value = !sidebarVisible.value;
  }
  function toggleDetail(): void {
    detailVisible.value = !detailVisible.value;
  }
  function openSettings(tab = 'general'): void {
    settingsOpen.value = true;
    settingsTab.value = tab;
    activeModal.value = 'settings';
  }
  function closeSettings(): void {
    settingsOpen.value = false;
    if (activeModal.value === 'settings') activeModal.value = null;
  }
  function openAbout(): void {
    activeModal.value = 'about';
  }
  function closeModal(): void {
    activeModal.value = null;
    settingsOpen.value = false;
  }
  function toggleCommandPalette(): void {
    commandPaletteOpen.value = !commandPaletteOpen.value;
  }
  function toggleShortcutHelp(): void {
    shortcutHelpOpen.value = !shortcutHelpOpen.value;
  }

  return {
    sidebarVisible,
    detailVisible,
    activeModal,
    toast,
    sidebarCollapsed,
    detailCollapsed,
    sidebarWidth,
    detailWidth,
    detailTab,
    collapsedTools,
    settingsOpen,
    settingsTab,
    commandPaletteOpen,
    shortcutHelpOpen,
    focusMode,
    themeMode,
    toggleSidebar,
    toggleDetail,
    openSettings,
    closeSettings,
    openAbout,
    closeModal,
    toggleCommandPalette,
    toggleShortcutHelp,
  };
});
