import { computed, ref } from 'vue';
import { defineStore } from 'pinia';
import { getHostBridge } from '@shared/bridge/hostBridge';
import type {
  BridgeResponse,
  DiscoveredProviderModel,
  FetchProviderModelsResult,
  ModelConfig,
  ProvidersConfigDocument,
} from '@shared/bridge/hostBridge';

/** 供应商模型目录快照 store：只读快照驻内存，不落盘；选择集仅接受新增态。 */
export const useModelCatalogStore = defineStore('modelCatalog', () => {
  const providerId = ref('');
  const snapshot = ref<FetchProviderModelsResult | null>(null);
  const loading = ref(false);
  const importing = ref(false);
  const ignoring = ref(false);
  const search = ref('');
  const selection = ref<Set<string>>(new Set());
  const showIgnored = ref(false);
  const lastError = ref<{ code?: string; message?: string } | null>(null);
  const fetchSeq = ref(0);

  const filtered = computed<DiscoveredProviderModel[]>(() => {
    const list = snapshot.value?.models ?? [];
    const keyword = search.value.trim().toLowerCase();
    return list.filter((m) => {
      if (m.state === 'ignored' && !showIgnored.value) return false;
      if (!keyword) return true;
      return m.id.toLowerCase().includes(keyword) || (m.ownedBy ?? '').toLowerCase().includes(keyword);
    });
  });

  const counts = computed(() => {
    const list = snapshot.value?.models ?? [];
    let added = 0;
    let existing = 0;
    let ignored = 0;
    for (const m of list) {
      if (m.state === 'existing') existing += 1;
      else if (m.state === 'ignored') ignored += 1;
      else added += 1;
    }
    return { total: list.length, new: added, existing, ignored, selected: selection.value.size };
  });

  const summaryText = computed(() => {
    const snap = snapshot.value;
    if (!snap) return '';
    return `${snap.modelsUrl} · 代理 ${snap.effectiveProxySummary} · 用时 ${snap.elapsedMs} ms`;
  });

  const canImport = computed(() => selection.value.size > 0 && !importing.value);
  // 最近一次拉取携带的草稿模型，供重试沿用同一二次确认输入
  const draftModels = ref<ModelConfig[] | null>(null);
  function markLocalState(ids: string[], state: 'existing' | 'ignored' | 'new'): void {
    if (!snapshot.value) return;
    const idSet = new Set(ids);
    for (const m of snapshot.value.models) {
      if (idSet.has(m.id)) m.state = state;
    }
  }

  /** 用当前草稿/已落盘模型对快照做二次确认（已忽视最高优先，不被复活）。 */
  function confirmExisting(models: readonly ModelConfig[] | null | undefined): void {
    if (!snapshot.value || !models?.length) return;
    const enabledById = new Map<string, boolean>();
    for (const m of models) {
      const id = String(m?.id ?? '').trim();
      if (!id) continue;
      const key = id.toLowerCase();
      if (!enabledById.has(key)) enabledById.set(key, m.enabled === true);
    }
    if (!enabledById.size) return;
    for (const m of snapshot.value.models) {
      if (m.state === 'ignored') continue; // 已忽视最高优先
      const hit = enabledById.get(m.id.trim().toLowerCase());
      if (hit === undefined) continue;
      m.state = 'existing';
      m.existsEnabled = hit;
      if (selection.value.has(m.id)) {
        const next = new Set(selection.value);
        next.delete(m.id);
        selection.value = next;
      }
    }
  }

  function reset(): void {
    providerId.value = '';
    snapshot.value = null;
    loading.value = false;
    importing.value = false;
    ignoring.value = false;
    search.value = '';
    selection.value = new Set();
    showIgnored.value = false;
    lastError.value = null;
    draftModels.value = null;
  }

  function clear(): void {
    selection.value = new Set();
  }

  function toggle(id: string): void {
    const target = snapshot.value?.models.find((m) => m.id === id);
    if (!target || target.state !== 'new') return;
    const next = new Set(selection.value);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    selection.value = next;
  }

  function selectAllFiltered(): void {
    const next = new Set(selection.value);
    for (const m of filtered.value) {
      if (m.state === 'new') next.add(m.id);
    }
    selection.value = next;
  }

  function setShowIgnored(v: boolean): void {
    showIgnored.value = v;
  }

  async function fetch(id: string, models?: readonly ModelConfig[] | null): Promise<BridgeResponse<FetchProviderModelsResult>> {
    const seq = fetchSeq.value + 1;
    fetchSeq.value = seq;
    providerId.value = id;
    draftModels.value = models ? ([...models] as ModelConfig[]) : null;
    loading.value = true;
    lastError.value = null;
    try {
      const res = await getHostBridge().fetchProviderModels(id);
      if (seq !== fetchSeq.value) return res;
      if (res.ok && res.data) {
        snapshot.value = res.data;
        selection.value = new Set();
        lastError.value = null;
        confirmExisting(draftModels.value);
      } else {
        snapshot.value = null;
        selection.value = new Set();
        lastError.value = { code: res.code ?? undefined, message: res.message ?? undefined };
      }
      return res;
    } catch (err) {
      if (seq !== fetchSeq.value) {
        return { ok: false, data: null as unknown as FetchProviderModelsResult };
      }
      snapshot.value = null;
      selection.value = new Set();
      lastError.value = { message: err instanceof Error ? err.message : String(err) };
      return { ok: false, data: null as unknown as FetchProviderModelsResult, message: lastError.value.message };
    } finally {
      if (seq === fetchSeq.value) loading.value = false;
    }
  }

  async function importSelected(importDisabled: boolean): Promise<BridgeResponse<ProvidersConfigDocument>> {
    const ids = [...selection.value];
    if (!providerId.value || ids.length === 0) {
      return { ok: false, message: '请先选择至少一个新增模型' };
    }
    importing.value = true;
    try {
      const res = await getHostBridge().importDiscoveredModels({
        providerId: providerId.value,
        modelIds: ids,
        importDisabled,
      });
      if (res.ok) {
        markLocalState(ids, 'existing');
        selection.value = new Set();
      }
      return res;
    } finally {
      importing.value = false;
    }
  }

  async function setIgnored(ids: string[], ignored: boolean): Promise<BridgeResponse<boolean>> {
    if (!providerId.value || ids.length === 0) {
      return { ok: false, message: '请先选择至少一个模型' };
    }
    ignoring.value = true;
    try {
      const res = await getHostBridge().setDiscoveredModelsIgnored(providerId.value, ids, ignored);
      if (res.ok) {
        markLocalState(ids, ignored ? 'ignored' : 'new');
        selection.value = new Set([...selection.value].filter((id) => !ids.includes(id)));
      }
      return res;
    } finally {
      ignoring.value = false;
    }
  }

  return {
    providerId,
    snapshot,
    loading,
    importing,
    ignoring,
    search,
    selection,
    showIgnored,
    lastError,
    fetchSeq,
    draftModels,
    filtered,
    counts,
    summaryText,
    canImport,
    reset,
    clear,
    toggle,
    selectAllFiltered,
    setShowIgnored,
    confirmExisting,
    fetch,
    importSelected,
    setIgnored,
  };
});
