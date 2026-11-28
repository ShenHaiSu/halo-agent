import { computed } from 'vue';
import { useMessage } from 'naive-ui';
import { useModelCatalogStore } from '@/stores/modelCatalogStore';
import type { ModelConfig } from '@shared/bridge/hostBridge';

/** 供应商模型目录交互封装：弹窗只包 store，不直调桥。 */
export function useModelCatalog() {
  const store = useModelCatalogStore();
  const message = useMessage();

  const canFetch = computed(() => !store.loading);

  async function open(providerId: string, draftModels?: readonly ModelConfig[] | null): Promise<void> {
    store.reset();
    store.search = '';
    await store.fetch(providerId, draftModels);
  }

  function close(): void {
    store.reset();
  }

  function toggle(id: string): void {
    store.toggle(id);
  }

  function selectAllFiltered(): void {
    store.selectAllFiltered();
  }

  function clear(): void {
    store.clear();
  }

  function setShowIgnored(v: boolean): void {
    store.setShowIgnored(v);
  }

  async function importSelected(importDisabled: boolean): Promise<void> {
    const res = await store.importSelected(importDisabled);
    if (res.ok) {
      // 刷新统一由 config:providers-changed 订阅与导入回调负责，此处不私自改 providersDoc
      message.success(importDisabled ? '已引入但不启用' : '已引入并启用');
    } else if (!res.ok) {
      message.error(res.message || '引入失败');
    }
  }

  async function setIgnored(ids: string[], ignored: boolean): Promise<void> {
    const res = await store.setIgnored(ids, ignored);
    if (!res.ok) message.error(res.message || '忽视操作失败');
  }

  return {
    store,
    canFetch,
    open,
    close,
    toggle,
    selectAllFiltered,
    clear,
    setShowIgnored,
    importSelected,
    setIgnored,
  };
}
