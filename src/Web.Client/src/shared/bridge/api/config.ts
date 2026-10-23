/** need21-F02: 配置域 API（旧 client.ts 网络/服务商/提示词/智能体配置方法迁移，签名不变）。 */
import type { BridgeTransport } from '../transport/transport';
import type {
  AgentConfigDocument,
  BridgeResponse,
  GlobalNetworkSettings,
  HostBridgeApi,
  PromptConfigDocument,
  PromptDiagnosticsDto,
  PromptPreviewDto,
  ProviderConfig,
  ProviderEndpointPreviewDto,
  ProvidersConfigDocument,
  ProxyConnectivityTestResult,
  SystemProxySnapshot,
} from '../protocol/types';
import {
  normalizeBridgePayload,
  normalizeNetworkSettingsDto,
  normalizeProvidersDocDto,
  sanitizeAgentConfigForSave,
  sanitizePromptConfigForSave,
} from './normalize';

export function createConfigApi(
  t: BridgeTransport
): Pick<
  HostBridgeApi,
  | 'getNetworkSettings'
  | 'saveNetworkSettings'
  | 'inspectSystemProxy'
  | 'testProxyConnectivity'
  | 'getProvidersConfig'
  | 'saveProvidersConfig'
  | 'upsertProvider'
  | 'deleteProvider'
  | 'setActiveModel'
  | 'previewProviderResolution'
  | 'getPromptConfig'
  | 'savePromptConfig'
  | 'resetPromptConfig'
  | 'getAgentConfig'
  | 'saveAgentConfig'
  | 'resetAgentConfig'
  | 'previewPrompt'
  | 'getPromptDiagnostics'
> {
  return {
    async getNetworkSettings() {
      const res = normalizeBridgePayload<BridgeResponse<GlobalNetworkSettings>>(await t.call('getNetworkSettings'));
      return normalizeNetworkSettingsDto(res);
    },
    async saveNetworkSettings(settings: GlobalNetworkSettings | string) {
      const payload = typeof settings === 'string' ? settings : JSON.stringify(settings);
      const res = normalizeBridgePayload<BridgeResponse<GlobalNetworkSettings>>(await t.call('saveNetworkSettings', payload));
      return normalizeNetworkSettingsDto(res);
    },
    async inspectSystemProxy(probeTargetUrl?: string) {
      return normalizeBridgePayload<BridgeResponse<SystemProxySnapshot>>(
        await t.call('inspectSystemProxy', probeTargetUrl ?? 'https://api.openai.com')
      );
    },
    async testProxyConnectivity(
      targetUrl?: string,
      overrideSettings?: GlobalNetworkSettings | string | null,
      provider?: ProviderConfig | string | null
    ) {
      const settingsStr =
        overrideSettings === null || overrideSettings === undefined
          ? ''
          : typeof overrideSettings === 'string'
            ? overrideSettings
            : JSON.stringify(overrideSettings);
      const providerStr =
        provider === null || provider === undefined ? '' : typeof provider === 'string' ? provider : JSON.stringify(provider);

      return normalizeBridgePayload<BridgeResponse<ProxyConnectivityTestResult>>(
        await t.call('testProxyConnectivity', targetUrl ?? '', settingsStr, providerStr)
      );
    },
    async getProvidersConfig() {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await t.call('getProvidersConfig'));
      return normalizeProvidersDocDto(res);
    },
    async saveProvidersConfig(doc: ProvidersConfigDocument | string) {
      const payload = typeof doc === 'string' ? doc : JSON.stringify(doc);
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await t.call('saveProvidersConfig', payload));
      return normalizeProvidersDocDto(res);
    },
    async upsertProvider(provider: ProviderConfig | string) {
      const payload = typeof provider === 'string' ? provider : JSON.stringify(provider);
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await t.call('upsertProvider', payload));
      return normalizeProvidersDocDto(res);
    },
    async deleteProvider(providerId: string) {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await t.call('deleteProvider', providerId));
      return normalizeProvidersDocDto(res);
    },
    async setActiveModel(providerId: string, modelId: string) {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await t.call('setActiveModel', providerId, modelId));
      return normalizeProvidersDocDto(res);
    },
    async previewProviderResolution(provider: ProviderConfig | string, modelId?: string, sessionId?: string, workspacePath?: string) {
      const payload = typeof provider === 'string' ? provider : JSON.stringify(provider);
      return normalizeBridgePayload<BridgeResponse<ProviderEndpointPreviewDto>>(
        await t.call('previewProviderResolution', payload, modelId ?? '', sessionId ?? '', workspacePath ?? '')
      );
    },
    async getPromptConfig() {
      return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await t.call('getPromptConfig'));
    },
    async savePromptConfig(config: PromptConfigDocument | string) {
      const payload = sanitizePromptConfigForSave(config);
      return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await t.call('savePromptConfig', payload));
    },
    async resetPromptConfig(blockId?: string) {
      return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await t.call('resetPromptConfig', blockId ?? ''));
    },
    async getAgentConfig() {
      return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await t.call('getAgentConfig'));
    },
    async saveAgentConfig(config: AgentConfigDocument | string) {
      const payload = sanitizeAgentConfigForSave(config);
      return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await t.call('saveAgentConfig', payload));
    },
    async resetAgentConfig(field?: string) {
      return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await t.call('resetAgentConfig', field ?? ''));
    },
    async previewPrompt(workspaceId: string) {
      return normalizeBridgePayload<BridgeResponse<PromptPreviewDto>>(await t.call('previewPrompt', workspaceId ?? ''));
    },
    async getPromptDiagnostics(workspaceId: string, sessionId?: string) {
      return normalizeBridgePayload<BridgeResponse<PromptDiagnosticsDto>>(
        await t.call('getPromptDiagnostics', workspaceId ?? '', sessionId ?? '')
      );
    },
  };
}
