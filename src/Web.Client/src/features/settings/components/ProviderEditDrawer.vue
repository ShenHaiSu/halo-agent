<template>
  <div v-if="provider" class="lg:col-span-8 space-y-3.5 min-w-0 max-w-full">
    <ProviderConnectionCard
      :provider="provider"
      :api-key="apiKey"
      :preview="preview"
      :proxy-summary="proxySummary"
      :chat-endpoint="chatEndpoint"
      :resp-endpoint="respEndpoint"
      @update:api-key="emit('update:api-key', $event)"
      @preview="emit('preview')"
      @proxy-change="emit('proxy-change', $event)"
      @change="emit('change', $event)"
    />
    <ProviderHeadersCard
      :provider="provider"
      :preview="preview"
      @preview="emit('preview')"
      @add-header="emit('add-header')"
      @add-opencode-header="emit('add-opencode-header')"
      @remove-header="emit('remove-header', $event)"
      @insert-var="emit('insert-var', $event)"
      @change="emit('change', $event)"
    />
    <ProviderModelList
      :models="provider.models"
      :provider-id="provider.id"
      :active-provider-id="activeProviderId"
      :active-model-id="activeModelId"
      :expanded-index="expandedIndex"
      :chat-endpoint="chatEndpoint"
      :resp-endpoint="respEndpoint"
      @toggle="emit('model-toggle', $event)"
      @add-model="emit('model-add')"
      @remove-model="emit('model-remove', $event)"
      @set-active="emit('model-set-active', $event)"
      @preset="(idx, field, value) => emit('model-preset', idx, field, value)"
      @clear-temp="emit('model-clear-temp', $event)"
      @vision="(idx, field, value) => emit('model-vision', idx, field, value)"
      @change-models="emit('models-change', $event)"
    />
    <div class="p-3 rounded-lg border border-subtle bg-surface space-y-2.5">
      <div class="flex items-center justify-between">
        <n-button size="small" type="error" ghost :disabled="providerCount <= 1" @click="emit('delete')">
          <template #icon>
            <Trash2 class="w-3.5 h-3.5" />
          </template>
          删除该服务商
        </n-button>
        <n-space :size="10">
          <n-button size="small" secondary :loading="testing" data-testid="test-provider-btn" @click="emit('test')">
            <template #icon>
              <Activity class="w-3.5 h-3.5" />
            </template>
            测试该服务商网关连通性
          </n-button>
          <n-button type="primary" size="small" :loading="saving" data-testid="save-provider-btn" @click="emit('save')">
            <template #icon>
              <Save class="w-3.5 h-3.5" />
            </template>
            保存服务商与模型配置
          </n-button>
        </n-space>
      </div>
      <div
        v-if="testResult"
        class="p-2.5 rounded border text-xs font-mono flex items-center justify-between"
        :class="
          testResult.success
            ? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-300'
            : 'border-rose-500/40 bg-rose-500/10 text-rose-300'
        "
      >
        <span>
          [{{ testResult.diagnosticCategory }}]
          {{ testResult.success ? `网关连通正常 (HTTP ${testResult.statusCode})` : testResult.errorMessage || '网关连接失败' }}
        </span>
        <span> 代理: {{ testResult.effectiveProxyUsed }} · {{ testResult.elapsedMs }} ms </span>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { Trash2, Activity, Save } from 'lucide-vue-next';
import type {
  ModelConfig,
  ProviderConfig,
  ProviderEndpointPreviewDto,
  ProviderProxyMode,
  ProxyConnectivityTestResult,
} from '@shared/bridge/hostBridge';
import type { VisionField } from '../composables/providerOptions';
import ProviderConnectionCard from './ProviderConnectionCard.vue';
import ProviderHeadersCard from './ProviderHeadersCard.vue';
import ProviderModelList from './ProviderModelList.vue';

defineProps<{
  provider: ProviderConfig | null;
  apiKey: string;
  preview: ProviderEndpointPreviewDto | null;
  proxySummary: string;
  chatEndpoint: string;
  respEndpoint: string;
  activeProviderId: string;
  activeModelId: string;
  expandedIndex: number | null;
  providerCount: number;
  testing: boolean;
  saving: boolean;
  testResult: ProxyConnectivityTestResult | null;
}>();
const emit = defineEmits<{
  'update:api-key': [v: string];
  change: [next: ProviderConfig];
  preview: [];
  'proxy-change': [mode: ProviderProxyMode];
  'add-header': [];
  'add-opencode-header': [];
  'remove-header': [idx: number];
  'insert-var': [token: string];
  'model-toggle': [idx: number];
  'model-add': [];
  'model-remove': [idx: number];
  'model-set-active': [modelId: string];
  'model-preset': [idx: number, field: 'maxContextTokens' | 'maxOutputTokens', value: number];
  'model-clear-temp': [idx: number];
  'model-vision': [idx: number, field: VisionField, value: unknown];
  'models-change': [models: ModelConfig[]];
  save: [];
  delete: [];
  test: [];
}>();
</script>
