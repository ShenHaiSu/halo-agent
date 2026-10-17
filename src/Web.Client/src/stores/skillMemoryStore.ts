import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  getHostBridge,
  haloEventBus,
  type SkillMemoryKind,
  type SkillMemoryScope,
  type SkillMemoryScopeFilter,
  type SkillMemoryListItem,
  type SkillMemoryDetail,
} from '@/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';

export const useSkillMemoryStore = defineStore('skillMemory', () => {
  const kind = ref<SkillMemoryKind>('skill');
  const scopeFilter = ref<SkillMemoryScopeFilter>('all');
  const keyword = ref('');
  const workspaceId = ref('');
  const items = ref<SkillMemoryListItem[]>([]);
  const loading = ref(false);
  const lastError = ref<string | null>(null);
  const preview = ref<SkillMemoryDetail | null>(null);
  const previewing = ref(false);

  let keywordTimer: number | null = null;
  let busOff: Array<() => void> = [];
  let busAttached = false;

  const filtered = computed<SkillMemoryListItem[]>(() => {
    const q = keyword.value.trim().toLowerCase();
    return items.value
      .filter((i) => (scopeFilter.value === 'all' ? true : i.scope === scopeFilter.value))
      .filter((i) => {
        if (!q) return true;
        return (
          i.name.toLowerCase().includes(q) ||
          (i.title || '').toLowerCase().includes(q) ||
          (i.summary || '').toLowerCase().includes(q)
        );
      });
  });

  function currentWorkspaceId(): string {
    if (workspaceId.value) return workspaceId.value;
    try {
      const ws = useWorkspaceStore();
      return ws.activeWorkspaceId || ws.activeWorkspace?.id || '';
    } catch {
      return '';
    }
  }

  async function reload(): Promise<void> {
    loading.value = true;
    lastError.value = null;
    try {
      const bridge = getHostBridge();
      const wid = currentWorkspaceId();
      const res = await bridge.listSkillMemory(kind.value, 'all', wid || undefined);
      if (res.ok && res.data) {
        items.value = [...res.data].sort((a, b) => a.name.localeCompare(b.name));
      } else {
        lastError.value = res.message || res.code || '加载失败';
      }
    } catch (err) {
      lastError.value = String(err);
    } finally {
      loading.value = false;
    }
  }

  function setKind(v: SkillMemoryKind): void {
    if (kind.value === v) return;
    kind.value = v;
    preview.value = null;
    void reload();
  }

  function setScope(v: SkillMemoryScopeFilter): void {
    scopeFilter.value = v;
  }

  function setKeyword(v: string): void {
    if (keywordTimer != null) window.clearTimeout(keywordTimer);
    keywordTimer = window.setTimeout(() => {
      keyword.value = v;
    }, 300);
  }

  function setWorkspace(v: string): void {
    workspaceId.value = v;
    void reload();
  }

  async function openPreview(scope: SkillMemoryScope, name: string): Promise<void> {
    previewing.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getSkillMemoryDetail(kind.value, scope, name, currentWorkspaceId() || undefined);
      if (res.ok && res.data) {
        preview.value = res.data;
      }
    } finally {
      previewing.value = false;
    }
  }

  function closePreview(): void {
    preview.value = null;
  }

  async function toggleEnabled(item: SkillMemoryListItem): Promise<{ ok: boolean; message?: string }> {
    const prev = item.enabled;
    item.enabled = !prev;
    try {
      const bridge = getHostBridge();
      const res = await bridge.setSkillMemoryEnabled(kind.value, item.scope, item.name, item.enabled, currentWorkspaceId() || undefined);
      if (res.ok && res.data) {
        return { ok: true, message: res.data.notice };
      }
      item.enabled = prev;
      return { ok: false, message: res.message || res.code || '开关失败' };
    } catch (err) {
      item.enabled = prev;
      return { ok: false, message: String(err) };
    }
  }

  async function removeItem(item: SkillMemoryListItem): Promise<{ ok: boolean; message?: string }> {
    try {
      const bridge = getHostBridge();
      const res = await bridge.deleteSkillMemory(kind.value, item.scope, item.name, currentWorkspaceId() || undefined);
      if (res.ok && res.data) {
        await reload();
        return { ok: true, message: res.data.notice };
      }
      return { ok: false, message: res.message || res.code || '删除失败' };
    } catch (err) {
      return { ok: false, message: String(err) };
    }
  }

  async function openFolder(item: SkillMemoryListItem): Promise<{ ok: boolean; message?: string }> {
    try {
      const bridge = getHostBridge();
      const res = await bridge.openSkillMemoryFolder(kind.value, item.scope, item.name, currentWorkspaceId() || undefined);
      if (res.ok) return { ok: true };
      return { ok: false, message: '目录已不存在，请刷新' };
    } catch {
      return { ok: false, message: '目录已不存在，请刷新' };
    }
  }

  function attachBus(): void {
    if (busAttached) return;
    busAttached = true;
    busOff = [
      haloEventBus.on('config:skill-memory-changed', () => {
        void reload();
      }),
      haloEventBus.on('storage:changed', () => {
        void reload();
      }),
    ];
  }

  function detachBus(): void {
    busAttached = false;
    for (const off of busOff) {
      try { off(); } catch { /* ignore */ }
    }
    busOff = [];
  }

  return {
    kind,
    scopeFilter,
    keyword,
    workspaceId,
    items,
    filtered,
    loading,
    lastError,
    preview,
    previewing,
    reload,
    setKind,
    setScope,
    setKeyword,
    setWorkspace,
    openPreview,
    closePreview,
    toggleEnabled,
    removeItem,
    openFolder,
    attachBus,
    detachBus,
    currentWorkspaceId,
  };
});
