import { computed, onMounted, ref, watch } from 'vue';
import { useMessage } from 'naive-ui';
import { useConfigStore } from '@/stores/configStore';
import type {
  ModelConfig,
  ProviderConfig,
  ProviderEndpointPreviewDto,
  ProviderProxyMode,
  ProxyConnectivityTestResult,
} from '@shared/bridge/hostBridge';
import { clampVisionForSave, normalizeAllVisionFields, versionPrefixOf } from './providerOptions';

export function useProviders() {
  const configStore = useConfigStore();
  const message = useMessage();
  const selectedProviderId = ref('');
  const editingProvider = ref<ProviderConfig | null>(null);
  const apiKeyInput = ref('');
  const endpointPreview = ref<ProviderEndpointPreviewDto | null>(null);
  const testingProvider = ref(false);
  const providerTestResult = ref<ProxyConnectivityTestResult | null>(null);
  const expandedModelIndex = ref<number | null>(null);
  const providers = computed(() => configStore.providersDoc.providers);
  const activeProviderId = computed(() => configStore.providersDoc.activeProviderId);
  const activeModelId = computed(() => configStore.providersDoc.activeModelId);
  const globalProxySummary = computed(() => configStore.globalProxySummaryText);
  const saving = computed(() => configStore.savingProvider);
  const versionPrefix = computed(() => versionPrefixOf(editingProvider.value?.baseUrl || ''));
  const computedChatEndpoint = computed(() => `${versionPrefix.value}/chat/completions`);
  const computedRespEndpoint = computed(() => `${versionPrefix.value}/responses`);
  function snapshot(id: string): void {
    const target = providers.value.find((p) => p.id === id);
    if (!target) return;
    selectedProviderId.value = id;
    editingProvider.value = JSON.parse(JSON.stringify(target)) as ProviderConfig;
    normalizeAllVisionFields(editingProvider.value);
    apiKeyInput.value = '';
    providerTestResult.value = null;
    expandedModelIndex.value = null;
    void refreshEndpointPreview();
  }
  function selectProvider(providerId: string): void {
    snapshot(providerId);
  }
  async function refreshEndpointPreview(): Promise<void> {
    if (!editingProvider.value) return;
    const res = await configStore.previewProviderResolution(editingProvider.value, editingProvider.value.models[0]?.id || 'gpt-4o');
    if (res.ok && res.data) endpointPreview.value = res.data;
  }
  function handleProviderProxyModeChange(mode: ProviderProxyMode): void {
    const p = editingProvider.value;
    if (!p) return;
    if (mode === 'Custom' && !p.customProxy) {
      p.customProxy = {
        scheme: 'http',
        host: '127.0.0.1',
        port: 7890,
        requiresAuth: false,
        bypassList: 'localhost;127.*',
        bypassOnLocal: true,
      };
    }
    void refreshEndpointPreview();
  }
  function addCustomHeader(): void {
    const p = editingProvider.value;
    if (!p) return;
    p.enableCustomHeaders = true;
    p.customHeaders.push({ key: 'X-Session-Hash', valueTemplate: '${session-hash}', enabled: true, isSecret: false });
    void refreshEndpointPreview();
  }
  function addOpencodeSessionHeader(): void {
    const p = editingProvider.value;
    if (!p) return;
    p.enableCustomHeaders = true;
    if (!p.customHeaders.some((h) => h.key.toLowerCase() === 'x-opencode-session')) {
      p.customHeaders.push({ key: 'x-opencode-session', valueTemplate: '${session-hash}', enabled: true, isSecret: false });
    }
    void refreshEndpointPreview();
  }
  function removeCustomHeader(index: number): void {
    const p = editingProvider.value;
    if (!p) return;
    p.customHeaders.splice(index, 1);
    void refreshEndpointPreview();
  }
  function insertVariableToLastHeader(token: string): void {
    const p = editingProvider.value;
    if (!p) return;
    p.enableCustomHeaders = true;
    if (p.customHeaders.length === 0) {
      p.customHeaders.push({ key: 'X-Custom-Trace', valueTemplate: token, enabled: true, isSecret: false });
    } else {
      const last = p.customHeaders[p.customHeaders.length - 1];
      last.valueTemplate = `${last.valueTemplate || ''}${token}`;
    }
    void refreshEndpointPreview();
  }
  function toggleModelExpand(idx: number): void {
    expandedModelIndex.value = expandedModelIndex.value === idx ? null : idx;
  }
  function setPreset(mdl: ModelConfig, field: 'maxContextTokens' | 'maxOutputTokens', value: number): void {
    mdl[field] = value;
  }
  function addModelRow(): void {
    const p = editingProvider.value;
    if (!p) return;
    p.models.push({
      id: `model-${p.models.length + 1}`,
      displayName: '',
      protocol: 'chat',
      maxContextTokens: 128000,
      maxOutputTokens: 4096,
      temperature: 0.2,
      enabled: true,
      supportsVision: false,
      maxImagesPerTurn: 4,
      visionDetail: 'high',
      includeReasoningInHistory: true,
    } as unknown as ModelConfig);
    expandedModelIndex.value = p.models.length - 1;
  }
  function removeModelRow(index: number): void {
    const p = editingProvider.value;
    if (!p || p.models.length <= 1) return;
    p.models.splice(index, 1);
    expandedModelIndex.value = null;
  }
  function isModelActive(providerId: string, modelId: string): boolean {
    return activeProviderId.value === providerId && activeModelId.value === modelId;
  }
  async function handleSetActiveModel(providerId: string, modelId: string): Promise<void> {
    if (!modelId.trim()) {
      message.warning('请先填写有效的模型 ID');
      return;
    }
    await handleSaveCurrentProvider(false);
    const res = await configStore.setActiveModel(providerId, modelId);
    if (res.ok) message.success(`已将 ${modelId} 设为当前默认调用模型`);
  }
  function handleAddNewProvider(): void {
    const newId = `provider-${Date.now().toString(36)}`;
    const fresh: ProviderConfig = {
      id: newId,
      name: '自定义 OpenAI 兼容服务商',
      enabled: true,
      baseUrl: 'https://api.openai.com/v1',
      apiKeyEncrypted: '',
      apiKeyMasked: '',
      hasApiKey: false,
      proxyMode: 'Inherit',
      customProxy: null,
      enableCustomHeaders: true,
      customHeaders: [
        { key: 'X-Session-Hash', valueTemplate: '${session-hash}', enabled: true, isSecret: false },
        { key: 'x-opencode-session', valueTemplate: '${session-hash}', enabled: true, isSecret: false },
      ],
      models: [
        {
          id: 'gpt-4o-mini',
          displayName: 'GPT-4o Mini',
          protocol: 'chat',
          maxContextTokens: 128000,
          maxOutputTokens: 4096,
          temperature: 0.2,
          enabled: true,
          supportsVision: false,
          maxImagesPerTurn: 4,
          visionDetail: 'high',
          includeReasoningInHistory: true,
        } as unknown as ModelConfig,
      ],
    };
    void configStore.upsertProvider(fresh).then((res) => {
      if (res.ok) {
        snapshot(newId);
        message.success('已创建新服务商，请配置 BaseURL 与 API Key');
      }
    });
  }
  async function handleSaveCurrentProvider(showToast = true): Promise<void> {
    const p = editingProvider.value;
    if (!p) return;
    if (!p.name.trim()) {
      message.warning('服务商名称不能为空');
      return;
    }
    const payload = JSON.parse(JSON.stringify(p)) as ProviderConfig;
    if (apiKeyInput.value.trim()) payload.apiKey = apiKeyInput.value.trim();
    for (const mdl of payload.models) clampVisionForSave(mdl);
    const res = await configStore.upsertProvider(payload);
    if (res.ok) {
      apiKeyInput.value = '';
      expandedModelIndex.value = null;
      await refreshEndpointPreview();
      if (showToast) message.success(res.message || '服务商配置已通过 DPAPI 加密落盘');
    } else {
      message.error(res.message || '保存服务商配置失败');
    }
  }
  async function handleDeleteCurrentProvider(): Promise<void> {
    const p = editingProvider.value;
    if (!p) return;
    const res = await configStore.deleteProvider(p.id);
    if (res.ok) message.success('服务商已删除');
    else message.error(res.message || '删除服务商失败');
  }
  async function handleTestCurrentProvider(): Promise<void> {
    const p = editingProvider.value;
    if (!p) return;
    testingProvider.value = true;
    providerTestResult.value = null;
    try {
      const payload = JSON.parse(JSON.stringify(p)) as ProviderConfig;
      if (apiKeyInput.value.trim()) payload.apiKey = apiKeyInput.value.trim();
      const res = await configStore.testProviderConnectivity(payload);
      if (res.data) providerTestResult.value = res.data;
      if (res.ok && res.data?.success) message.success(`服务商网关连通正常 (${res.data.elapsedMs} ms)`);
      else message.warning(res.data?.errorMessage || res.message || '服务商网关连接异常');
    } finally {
      testingProvider.value = false;
    }
  }
  watch(
    () => configStore.providersDoc.providers,
    (list) => {
      if (!list.length) {
        editingProvider.value = null;
        return;
      }
      if (!selectedProviderId.value || !list.some((p) => p.id === selectedProviderId.value)) {
        snapshot(configStore.providersDoc.activeProviderId || list[0].id);
      } else {
        const current = list.find((p) => p.id === selectedProviderId.value);
        if (current) {
          editingProvider.value = JSON.parse(JSON.stringify(current)) as ProviderConfig;
          normalizeAllVisionFields(editingProvider.value);
        }
      }
    },
    { immediate: true, deep: true }
  );
  watch(
    () => editingProvider.value?.id,
    () => (expandedModelIndex.value = null)
  );
  onMounted(() => void refreshEndpointPreview());
  return {
    providers,
    activeProviderId,
    activeModelId,
    globalProxySummary,
    saving,
    selectedProviderId,
    editingProvider,
    apiKeyInput,
    endpointPreview,
    testingProvider,
    providerTestResult,
    expandedModelIndex,
    computedChatEndpoint,
    computedRespEndpoint,
    selectProvider,
    refreshEndpointPreview,
    handleProviderProxyModeChange,
    addCustomHeader,
    addOpencodeSessionHeader,
    removeCustomHeader,
    insertVariableToLastHeader,
    toggleModelExpand,
    setPreset,
    addModelRow,
    removeModelRow,
    isModelActive,
    handleSetActiveModel,
    handleAddNewProvider,
    handleSaveCurrentProvider,
    handleDeleteCurrentProvider,
    handleTestCurrentProvider,
    setApiKey: (v: string) => (apiKeyInput.value = v),
    replaceEditing: (next: ProviderConfig) => (editingProvider.value = next),
    replaceModels: (models: ModelConfig[]) => {
      if (editingProvider.value) editingProvider.value.models = models;
    },
  };
}
export type Providers = ReturnType<typeof useProviders>;
