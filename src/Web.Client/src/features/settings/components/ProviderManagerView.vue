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
      :active-provider-id="p.activeProviderId"
      :active-model-id="p.activeModelId"
      :expanded-index="p.expandedModelIndex"
      :provider-count="p.providers.length"
      :testing="p.testingProvider"
      :saving="p.saving"
      :test-result="p.providerTestResult"
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
    />
  </div>
</template>
<script setup lang="ts">
import { reactive } from 'vue';
import ProviderListView from './ProviderListView.vue';
import ProviderEditDrawer from './ProviderEditDrawer.vue';
import { useProviders } from '../composables/useProviders';
import { setVisionField, type VisionField } from '../composables/providerOptions';

const p = reactive(useProviders());
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
