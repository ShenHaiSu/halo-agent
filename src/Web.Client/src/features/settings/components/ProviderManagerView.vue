<template>
  <div class="grid grid-cols-1 lg:grid-cols-12 gap-4 min-w-0 max-w-full overflow-x-hidden" data-testid="provider-manager-panel">
    <ProviderListView
      :providers="p.providers"
      :active-provider-id="p.activeProviderId"
      :selected-id="p.selectedProviderId"
      @select="p.selectProvider($event)"
      @add="p.handleAddNewProvider()"
    />
    <ProviderEditDrawer
      :provider="p.editingProvider"
      :api-key="p.apiKeyInput"
      :preview="p.endpointPreview"
      :proxy-summary="p.globalProxySummary"
      :chat-endpoint="p.computedChatEndpoint"
      :resp-endpoint="p.computedRespEndpoint"
      :models-endpoint="p.computedModelsEndpoint"
      :active-provider-id="p.activeProviderId"
      :active-model-id="p.activeModelId"
      :expanded-index="p.expandedModelIndex"
      :provider-count="p.providers.length"
      :testing="p.testingProvider"
      :saving="p.saving"
      :test-result="p.providerTestResult"
      :new-count="catalog.store.counts.new"
      :ignored-count="catalog.store.counts.ignored"
      :fetch-disabled="fetchDisabled"
      :fetch-tip="fetchTip"
      @update:api-key="p.setApiKey($event)"
      @change="p.replaceEditing($event)"
      @preview="p.refreshEndpointPreview()"
      @proxy-change="p.handleProviderProxyModeChange($event)"
      @add-header="p.addCustomHeader()"
      @add-opencode-header="p.addOpencodeSessionHeader()"
      @remove-header="p.removeCustomHeader($event)"
      @insert-var="p.insertVariableToLastHeader($event)"
      @model-toggle="p.toggleModelExpand($event)"
      @model-add="p.addModelRow()"
      @model-remove="p.removeModelRow($event)"
      @model-set-active="p.handleSetActiveModel(p.editingProvider?.id || '', $event)"
      @model-preset="(idx, field, value) => onPreset(idx, field, value)"
      @model-clear-temp="onClearTemp($event)"
      @model-vision="(idx, field, value) => onVision(idx, field, value)"
      @models-change="p.replaceModels($event)"
      @save="p.handleSaveCurrentProvider()"
      @delete="p.handleDeleteCurrentProvider()"
      @test="p.handleTestCurrentProvider()"
      @fetch-models="openCatalog"
      @goto-fetch="openCatalogFromAnchor"
    />
    <ProviderModelCatalogDialog
      :show="catalogOpen"
      :provider-name="p.editingProvider?.name || ''"
      @close="closeCatalog"
      @imported="onCatalogImported"
      @goto-headers="onGotoHeaders"
      @goto-baseurl="onGotoBaseUrl"
      @set-default="onCatalogSetDefault"
    />
  </div>
</template>
<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import ProviderListView from './ProviderListView.vue';
import ProviderEditDrawer from './ProviderEditDrawer.vue';
import ProviderModelCatalogDialog from './ProviderModelCatalogDialog.vue';
import { useProviders } from '../composables/useProviders';
import { useModelCatalog } from '../composables/useModelCatalog';
import { useConfigStore } from '@/stores/configStore';
import { setVisionField, type VisionField } from '../composables/providerOptions';

const p = reactive(useProviders());
const catalog = useModelCatalog();
const configStore = useConfigStore();
const catalogOpen = ref(false);
const fetchDisabled = computed(() => {
  if (!p.editingProvider) return true;
  if (!p.catalogSupported) return true;
  const baseUrl = (p.editingProvider.baseUrl || '').trim();
  if (!baseUrl) return true;
  return catalog.store.loading;
});
const fetchTip = computed(() => {
  if (!p.editingProvider) return '请先选择服务商';
  if (!p.catalogSupported) return '宿主过旧，请升级';
  if (!(p.editingProvider.baseUrl || '').trim()) return '请先填写 BaseURL';
  return '拉取供应商模型';
});
async function openCatalog(): Promise<void> {
  if (fetchDisabled.value || !p.editingProvider) return;
  catalogOpen.value = true;
  await catalog.open(p.editingProvider.id, p.editingProvider.models);
}
async function openCatalogFromAnchor(): Promise<void> {
  if (fetchDisabled.value || !p.editingProvider) return;
  try {
    document.querySelector('[data-testid="provider-model-list-card"]')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  } catch {
    // 滚动失败不阻塞开弹窗
  }
  catalogOpen.value = true;
  await catalog.open(p.editingProvider.id, p.editingProvider.models);
}
function closeCatalog(): void {
  catalogOpen.value = false;
  catalog.close();
}
async function onCatalogImported(): Promise<void> {
  catalogOpen.value = false;
  // 先换 providersDoc（抽屉与徽标真数），再带草稿重拉目录做二次确认
  await configStore.refreshProvidersConfig();
  await catalog.store.fetch(catalog.store.providerId || p.editingProvider?.id || '', p.editingProvider?.models);
  await p.refreshEndpointPreview();
}
async function onCatalogSetDefault(modelId: string): Promise<void> {
  if (!p.editingProvider) return;
  const providerId = p.editingProvider.id;
  catalogOpen.value = false;
  catalog.close();
  await p.handleSetActiveModel(providerId, modelId);
}
function flashHeadersCard(): void {
  const card = document.querySelector('[data-testid="provider-headers-card"]');
  if (!card) return;
  card.classList.add('catalog-flash');
  setTimeout(() => card.classList.remove('catalog-flash'), 1600);
}
function onGotoHeaders(): void {
  catalogOpen.value = false;
  catalog.close();
  flashHeadersCard();
}
function onGotoBaseUrl(): void {
  catalogOpen.value = false;
  catalog.close();
  const input = document.querySelector<HTMLInputElement>('[data-testid="provider-baseurl-input"] input');
  if (input) {
    input.focus();
    input.select();
  }
}
function onPreset(idx: number, field: 'maxContextTokens' | 'maxOutputTokens', value: number): void {
  const mdl = p.editingProvider?.models[idx];
  if (mdl) p.setPreset(mdl, field, value);
}
function onClearTemp(idx: number): void {
  const mdl = p.editingProvider?.models[idx];
  if (mdl) mdl.temperature = null;
}
function onVision(idx: number, field: VisionField, value: unknown): void {
  const mdl = p.editingProvider?.models[idx];
  if (mdl) setVisionField(mdl, field, value);
}
</script>
