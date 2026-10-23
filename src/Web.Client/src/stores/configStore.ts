import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  getHostBridge,
  haloEventBus,
  type GlobalNetworkSettings,
  type SystemProxySnapshot,
  type ProxyConnectivityTestResult,
  type ProvidersConfigDocument,
  type ProviderConfig,
  type ModelConfig,
  type ProviderEndpointPreviewDto,
  type BridgeResponse,
} from '@shared/bridge/hostBridge';

export const useConfigStore = defineStore('config', () => {
  const networkSettings = ref<GlobalNetworkSettings>({
    mode: 'System',
    customProxy: {
      scheme: 'http',
      host: '127.0.0.1',
      port: 7890,
      requiresAuth: false,
      username: '',
      passwordEncrypted: '',
      passwordMasked: '',
      bypassList: 'localhost;127.*;10.*;192.168.*',
      bypassOnLocal: true,
    },
    defaultConnectTimeoutSeconds: 15,
    defaultRequestTimeoutSeconds: 300,
  });

  const systemProxySnapshot = ref<SystemProxySnapshot | null>(null);

  const providersDoc = ref<ProvidersConfigDocument>({
    activeProviderId: '',
    activeModelId: '',
    providers: [],
  });

  const loading = ref(false);
  const savingNetwork = ref(false);
  const savingProvider = ref(false);
  const testingProxy = ref(false);
  const lastProxyTestResult = ref<ProxyConnectivityTestResult | null>(null);

  const eventUnsubscribers: Array<() => void> = [];

  const activeProvider = computed<ProviderConfig | undefined>(() => {
    const list = providersDoc.value.providers;
    if (!list.length) return undefined;
    return list.find((p) => p.id === providersDoc.value.activeProviderId) || list.find((p) => p.enabled) || list[0];
  });

  const activeModel = computed<ModelConfig | undefined>(() => {
    const provider = activeProvider.value;
    if (!provider || !provider.models.length) return undefined;
    return (
      provider.models.find((m) => m.id === providersDoc.value.activeModelId) || provider.models.find((m) => m.enabled) || provider.models[0]
    );
  });

  const globalProxySummaryText = computed<string>(() => {
    const mode = networkSettings.value.mode;
    if (mode === 'None') {
      return 'DIRECT (强制直连)';
    }
    if (mode === 'Custom') {
      const cp = networkSettings.value.customProxy;
      return `CUSTOM (${cp.scheme}://${cp.host}:${cp.port})`;
    }
    if (systemProxySnapshot.value?.isEnabled && systemProxySnapshot.value.effectiveHttpProxyUri) {
      return `SYSTEM (${systemProxySnapshot.value.effectiveHttpProxyUri})`;
    }
    return 'SYSTEM (系统直连)';
  });

  async function initialize(): Promise<void> {
    loading.value = true;
    try {
      await Promise.all([refreshNetworkSettings(), refreshSystemProxySnapshot(), refreshProvidersConfig()]);

      if (eventUnsubscribers.length === 0) {
        eventUnsubscribers.push(
          haloEventBus.on('storage:changed', () => {
            refreshNetworkSettings();
            refreshProvidersConfig();
          })
        );
      }
    } finally {
      loading.value = false;
    }
  }

  async function refreshNetworkSettings(): Promise<void> {
    const bridge = getHostBridge();
    const res = await bridge.getNetworkSettings();
    if (res.ok && res.data) {
      networkSettings.value = res.data;
    }
  }

  async function refreshSystemProxySnapshot(probeTargetUrl = 'https://api.openai.com'): Promise<SystemProxySnapshot | null> {
    const bridge = getHostBridge();
    const res = await bridge.inspectSystemProxy(probeTargetUrl);
    if (res.ok && res.data) {
      systemProxySnapshot.value = res.data;
      return res.data;
    }
    return null;
  }

  async function saveNetworkSettings(newSettings: GlobalNetworkSettings): Promise<BridgeResponse<GlobalNetworkSettings>> {
    savingNetwork.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.saveNetworkSettings(newSettings);
      if (res.ok && res.data) {
        networkSettings.value = res.data;
      }
      await refreshSystemProxySnapshot();
      return res;
    } finally {
      savingNetwork.value = false;
    }
  }

  async function testGlobalProxyConnectivity(
    targetUrl?: string,
    overrideSettings?: GlobalNetworkSettings | null
  ): Promise<BridgeResponse<ProxyConnectivityTestResult>> {
    testingProxy.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.testProxyConnectivity(
        targetUrl || 'https://api.openai.com/v1/models',
        overrideSettings ?? networkSettings.value,
        null
      );
      if (res.data) {
        lastProxyTestResult.value = res.data;
      }
      return res;
    } finally {
      testingProxy.value = false;
    }
  }

  async function refreshProvidersConfig(): Promise<void> {
    const bridge = getHostBridge();
    const res = await bridge.getProvidersConfig();
    if (res.ok && res.data) {
      providersDoc.value = res.data;
    }
  }

  async function saveProvidersConfig(doc: ProvidersConfigDocument): Promise<BridgeResponse<ProvidersConfigDocument>> {
    savingProvider.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.saveProvidersConfig(doc);
      if (res.ok && res.data) {
        providersDoc.value = res.data;
      }
      return res;
    } finally {
      savingProvider.value = false;
    }
  }

  async function upsertProvider(provider: ProviderConfig): Promise<BridgeResponse<ProvidersConfigDocument>> {
    savingProvider.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.upsertProvider(provider);
      if (res.ok && res.data) {
        providersDoc.value = res.data;
      }
      return res;
    } finally {
      savingProvider.value = false;
    }
  }

  async function deleteProvider(providerId: string): Promise<BridgeResponse<ProvidersConfigDocument>> {
    savingProvider.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.deleteProvider(providerId);
      if (res.ok && res.data) {
        providersDoc.value = res.data;
      }
      return res;
    } finally {
      savingProvider.value = false;
    }
  }

  async function setActiveModel(providerId: string, modelId: string): Promise<BridgeResponse<ProvidersConfigDocument>> {
    const bridge = getHostBridge();
    const res = await bridge.setActiveModel(providerId, modelId);
    if (res.ok && res.data) {
      providersDoc.value = res.data;
    }
    return res;
  }

  async function previewProviderResolution(
    provider: ProviderConfig,
    modelId?: string,
    sessionId?: string,
    workspacePath?: string
  ): Promise<BridgeResponse<ProviderEndpointPreviewDto>> {
    const bridge = getHostBridge();
    return bridge.previewProviderResolution(provider, modelId, sessionId, workspacePath);
  }

  async function testProviderConnectivity(
    provider: ProviderConfig,
    targetUrl?: string
  ): Promise<BridgeResponse<ProxyConnectivityTestResult>> {
    const bridge = getHostBridge();
    return bridge.testProxyConnectivity(targetUrl || '', null, provider);
  }

  return {
    networkSettings,
    systemProxySnapshot,
    providersDoc,
    loading,
    savingNetwork,
    savingProvider,
    testingProxy,
    lastProxyTestResult,
    activeProvider,
    activeModel,
    globalProxySummaryText,
    initialize,
    refreshNetworkSettings,
    refreshSystemProxySnapshot,
    saveNetworkSettings,
    testGlobalProxyConnectivity,
    refreshProvidersConfig,
    saveProvidersConfig,
    upsertProvider,
    deleteProvider,
    setActiveModel,
    previewProviderResolution,
    testProviderConnectivity,
  };
});
