import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useModelCatalogStore } from '@/stores/modelCatalogStore';
import type { FetchProviderModelsResult, ModelConfig } from '@shared/bridge/hostBridge';

const snapshot: FetchProviderModelsResult = {
  providerId: 'provider-openai',
  modelsUrl: 'https://api.openai.com/v1/models',
  effectiveProxySummary: 'DIRECT (No Proxy)',
  elapsedMs: 12,
  models: [
    { id: 'glm-5.3', ownedBy: 'openai', created: 1626777600, state: 'new', existsEnabled: false },
    { id: 'gpt-4o', ownedBy: 'openai', created: 1626777600, state: 'existing', existsEnabled: true },
    { id: 'gpt-6-astra', ownedBy: 'openai', created: 1626777600, state: 'ignored', existsEnabled: false },
    { id: 'kimi-k3', ownedBy: 'openai', created: 1626777600, state: 'new', existsEnabled: false },
  ],
};

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return {
    ...actual,
    getHostBridge: () => ({
      fetchProviderModels: async (providerId: string) => {
        await new Promise((resolve) => setTimeout(resolve, providerId === 'slow' ? 30 : 5));
        const fresh = JSON.parse(JSON.stringify(snapshot)) as FetchProviderModelsResult;
        if (providerId === 'slow') {
          return { ok: true, data: { ...fresh, providerId } };
        }
        return { ok: true, data: fresh };
      },
      importDiscoveredModels: async () => ({
        ok: true,
        data: { activeProviderId: 'provider-openai', activeModelId: 'gpt-4o', providers: [] },
      }),
      setDiscoveredModelsIgnored: async () => ({ ok: true, data: true }),
    }),
  };
});

describe('modelCatalogStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('拉取后计数与搜索过滤正确', async () => {
    const store = useModelCatalogStore();
    const res = await store.fetch('provider-openai');
    expect(res.ok).toBe(true);
    expect(store.counts.total).toBe(4);
    expect(store.counts.new).toBe(2);
    expect(store.counts.existing).toBe(1);
    expect(store.counts.ignored).toBe(1);

    store.search = 'glm';
    expect(store.filtered.map((m) => m.id)).toEqual(['glm-5.3']);

    store.search = '';
    store.setShowIgnored(false);
    expect(store.filtered.some((m) => m.state === 'ignored')).toBe(false);
    store.setShowIgnored(true);
    expect(store.filtered.some((m) => m.state === 'ignored')).toBe(true);
  });

  it('仅新增态可选中，全选仅收新增', async () => {
    const store = useModelCatalogStore();
    await store.fetch('provider-openai');

    store.toggle('gpt-4o');
    store.toggle('gpt-6-astra');
    expect(store.selection.size).toBe(0);

    store.toggle('glm-5.3');
    expect(store.selection.has('glm-5.3')).toBe(true);

    store.selectAllFiltered();
    expect(store.selection.has('kimi-k3')).toBe(true);
    expect(store.selection.has('gpt-4o')).toBe(false);

    store.clear();
    expect(store.selection.size).toBe(0);
  });

  it('过期回包丢弃：先返后到者胜', async () => {
    const store = useModelCatalogStore();
    const slow = store.fetch('slow');
    const fast = store.fetch('provider-openai');
    await Promise.all([slow, fast]);
    expect(store.providerId).toBe('provider-openai');
    expect(store.snapshot?.modelsUrl).toBe('https://api.openai.com/v1/models');
  });

  it('导入成功本地翻为已存在并清空选择', async () => {
    const store = useModelCatalogStore();
    await store.fetch('provider-openai');
    store.toggle('glm-5.3');
    const res = await store.importSelected(false);
    expect(res.ok).toBe(true);
    expect(store.selection.size).toBe(0);
    expect(store.snapshot?.models.find((m) => m.id === 'glm-5.3')?.state).toBe('existing');
  });

  it('忽视成功本地翻态', async () => {
    const store = useModelCatalogStore();
    await store.fetch('provider-openai');
    const res = await store.setIgnored(['kimi-k3'], true);
    expect(res.ok).toBe(true);
    expect(store.snapshot?.models.find((m) => m.id === 'kimi-k3')?.state).toBe('ignored');
  });

  it('草稿命中模型翻为已存在并剔除选择（大小写与首尾空格归一）', async () => {
    const store = useModelCatalogStore();
    await store.fetch('provider-openai');
    store.toggle('glm-5.3');
    expect(store.selection.has('glm-5.3')).toBe(true);

    store.confirmExisting([{ id: '  GLM-5.3  ', enabled: true } as ModelConfig]);

    const hit = store.snapshot?.models.find((m) => m.id === 'glm-5.3');
    expect(hit?.state).toBe('existing');
    expect(hit?.existsEnabled).toBe(true);
    expect(store.selection.has('glm-5.3')).toBe(false);
  });

  it('已忽视模型不被草稿复活', async () => {
    const store = useModelCatalogStore();
    await store.fetch('provider-openai');
    store.confirmExisting([{ id: 'gpt-6-astra', enabled: true } as ModelConfig]);
    expect(store.snapshot?.models.find((m) => m.id === 'gpt-6-astra')?.state).toBe('ignored');
  });

  it('空草稿对快照零副作用', async () => {
    const store = useModelCatalogStore();
    await store.fetch('provider-openai');
    const before = store.counts.new;
    store.confirmExisting([]);
    store.confirmExisting(null);
    expect(store.counts.new).toBe(before);
  });

  it('拉取携带草稿即做二次确认', async () => {
    const store = useModelCatalogStore();
    await store.fetch('provider-openai', [{ id: 'kimi-k3', enabled: false } as ModelConfig]);
    const hit = store.snapshot?.models.find((m) => m.id === 'kimi-k3');
    expect(hit?.state).toBe('existing');
    expect(hit?.existsEnabled).toBe(false);
    expect(store.counts.new).toBe(1);
  });
});
