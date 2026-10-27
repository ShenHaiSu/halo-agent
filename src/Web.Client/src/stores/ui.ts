// src/Web.Client/src/stores/ui.ts
// D4-8 UI 状态 store。
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { useToast } from '@/composables/useToast';

export type ModalKey = 'settings' | 'about' | 'permissions' | null;

/**
 * UI 状态 store。
 *
 * ============ 只管"界面本身的状态" ============
 *  侧栏折叠、当前打开的模态、toast。
 *  不存业务数据（那是 session/messages 的职责）。
 * ============ ============
 */
export const useUiStore = defineStore('ui', () => {
  const sidebarVisible = ref(true);
  const detailVisible = ref(false);
  const activeModal = ref<ModalKey>(null);
  const toast = useToast();

  function toggleSidebar(): void {
    sidebarVisible.value = !sidebarVisible.value;
  }

  function toggleDetail(): void {
    detailVisible.value = !detailVisible.value;
  }

  function openSettings(): void {
    activeModal.value = 'settings';
  }

  function openAbout(): void {
    activeModal.value = 'about';
  }

  function closeModal(): void {
    activeModal.value = null;
  }

  return {
    sidebarVisible,
    detailVisible,
    activeModal,
    toast,
    toggleSidebar,
    toggleDetail,
    openSettings,
    openAbout,
    closeModal,
  };
});
