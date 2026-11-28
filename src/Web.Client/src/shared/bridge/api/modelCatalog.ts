/** 供应商模型目录 API（拉取只读先行；写方法后续阶段启用）。 */
import type { BridgeTransport } from '../transport/transport';
import type {
  BridgeResponse,
  FetchProviderModelsResult,
  HostBridgeApi,
  ImportDiscoveredModelsRequest,
  ProvidersConfigDocument,
} from '../protocol/types';
import { normalizeBridgePayload, normalizeCatalogDto, normalizeProvidersDocDto } from './normalize';

export function createModelCatalogApi(
  t: BridgeTransport
): Pick<HostBridgeApi, 'fetchProviderModels' | 'importDiscoveredModels' | 'setDiscoveredModelsIgnored'> {
  return {
    async fetchProviderModels(providerId: string) {
      const res = normalizeBridgePayload<BridgeResponse<FetchProviderModelsResult>>(await t.call('fetchProviderModels', providerId ?? ''));
      return normalizeCatalogDto(res);
    },
    async importDiscoveredModels(req: ImportDiscoveredModelsRequest | string) {
      const payload = typeof req === 'string' ? req : JSON.stringify(req);
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await t.call('importDiscoveredModels', payload));
      return normalizeProvidersDocDto(res);
    },
    async setDiscoveredModelsIgnored(providerId: string, modelIds: string[], ignored: boolean) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await t.call('setDiscoveredModelsIgnored', providerId ?? '', JSON.stringify(modelIds ?? []), ignored)
      );
    },
  };
}
