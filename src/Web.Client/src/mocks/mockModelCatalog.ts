/** 供应商模型目录 Mock：双源各取 6 条拼快照，三态齐备；写方法直接改 Mock 文档。 */
import { normalizeVersionedBaseUrlClient } from '@shared/bridge/api/normalize';
import type {
  DiscoveredProviderModel,
  FetchProviderModelsResult,
  HostBridgeApi,
  ImportDiscoveredModelsRequest,
} from '@shared/bridge/protocol/types';
import type { MockState } from './mockState';

const mockDiscovered: DiscoveredProviderModel[] = [
  { id: 'glm-5.3', ownedBy: 'openai', created: 1626777600, state: 'new', existsEnabled: false },
  { id: 'kimi-k3', ownedBy: 'openai', created: 1626777600, state: 'new', existsEnabled: false },
  { id: 'deepseek-v4-pro', ownedBy: 'opencode', created: 1795869219, state: 'new', existsEnabled: false },
  { id: 'beam-free', ownedBy: 'opencode', created: 1795869219, state: 'existing', existsEnabled: true },
  { id: 'gpt-4o', ownedBy: 'openai', created: 1626777600, state: 'existing', existsEnabled: false },
  { id: 'gpt-6-astra', ownedBy: 'openai', created: 1626777600, state: 'ignored', existsEnabled: false },
];

const mockIgnore: Record<string, Set<string>> = {};

function currentSnapshot(providerId: string): DiscoveredProviderModel[] {
  const ignored = mockIgnore[providerId] ?? new Set<string>();
  return mockDiscovered.map((m) => {
    if (ignored.has(m.id.toLowerCase())) return { ...m, state: 'ignored' as const, existsEnabled: false };
    return { ...m };
  });
}

export function mockModelCatalogMethods(s: MockState): Partial<HostBridgeApi> {
  return {
    async fetchProviderModels(providerId: string) {
      await new Promise((resolve) => setTimeout(resolve, 300));
      const provider = s.mockProvidersDoc.providers.find((p) => p.id === providerId);
      if (!provider) {
        return { ok: false, code: 'CATALOG_PROVIDER_NOT_FOUND', message: '服务商不存在，请刷新后重试' };
      }
      const prefix = normalizeVersionedBaseUrlClient(provider.baseUrl);
      const data: FetchProviderModelsResult = {
        providerId: provider.id,
        modelsUrl: `${prefix}/models`,
        effectiveProxySummary: 'DIRECT (No Proxy)',
        elapsedMs: 300,
        models: currentSnapshot(provider.id).map((m) => {
          const existing = provider.models.find((x) => x.id.toLowerCase() === m.id.toLowerCase());
          if (m.state !== 'ignored' && existing) {
            return { ...m, state: 'existing' as const, existsEnabled: existing.enabled };
          }
          return { ...m };
        }),
      };
      return { ok: true, data };
    },
    async importDiscoveredModels(req: ImportDiscoveredModelsRequest | string) {
      const parsed: ImportDiscoveredModelsRequest = typeof req === 'string' ? JSON.parse(req) : JSON.parse(JSON.stringify(req));
      const provider = s.mockProvidersDoc.providers.find((p) => p.id === parsed.providerId);
      if (!provider) {
        return { ok: false, code: 'CATALOG_PROVIDER_NOT_FOUND', message: '服务商不存在，请刷新后重试' };
      }
      const ids = [...new Set((parsed.modelIds ?? []).map((id) => String(id).trim()).filter(Boolean))];
      for (const id of ids) {
        const existing = provider.models.find((m) => m.id.toLowerCase() === id.toLowerCase());
        if (existing) {
          if (!parsed.importDisabled && !existing.enabled) existing.enabled = true;
          continue;
        }
        provider.models.push({
          id,
          displayName: id,
          protocol: 'chat',
          maxContextTokens: 128000,
          maxOutputTokens: 4096,
          temperature: 0.2,
          enabled: !parsed.importDisabled,
          supportsVision: false,
          maxImagesPerTurn: 4,
          visionDetail: 'high',
          includeReasoningInHistory: true,
        });
        mockIgnore[provider.id]?.delete(id.toLowerCase());
      }
      return {
        ok: true,
        message: '已批量引入供应商模型 (Mock)',
        data: JSON.parse(JSON.stringify(s.mockProvidersDoc)),
      };
    },
    async setDiscoveredModelsIgnored(providerId: string, modelIds: string[], ignored: boolean) {
      const provider = s.mockProvidersDoc.providers.find((p) => p.id === providerId);
      if (!provider) {
        return { ok: false, code: 'CATALOG_PROVIDER_NOT_FOUND', message: '服务商不存在，请刷新后重试' };
      }
      const set = (mockIgnore[providerId] ??= new Set<string>());
      for (const raw of modelIds ?? []) {
        const id = String(raw ?? '')
          .trim()
          .toLowerCase();
        if (!id) continue;
        if (ignored) set.add(id);
        else set.delete(id);
      }
      return { ok: true, data: true };
    },
  };
}
