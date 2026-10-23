<template>
  <n-card size="small" title="服务商网关、密钥与代理策略">
    <template #header-extra>
      <div class="flex items-center gap-2">
        <span class="text-xs text-secondary">启用该服务商</span>
        <n-switch :value="provider.enabled" size="small" @update:value="patch((p) => (p.enabled = $event))" />
      </div>
    </template>
    <div class="space-y-3">
      <div class="grid grid-cols-12 gap-3">
        <div class="col-span-5 space-y-1">
          <label class="text-xs text-secondary">服务商名称 (Name)</label>
          <n-input
            :value="provider.name"
            placeholder="例如 OpenAI / DeepSeek / 企业网关"
            size="small"
            data-testid="provider-name-input"
            @update:value="patch((p) => (p.name = $event))"
          />
        </div>
        <div class="col-span-7 space-y-1">
          <div class="flex items-center justify-between">
            <label class="text-xs text-secondary">
              API Base URL (末尾加 <code class="font-mono text-amber-300">#</code> 可禁用自动补齐 /v1)
            </label>
          </div>
          <n-input
            :value="provider.baseUrl"
            placeholder="https://api.openai.com/v1"
            size="small"
            class="font-mono text-xs"
            data-testid="provider-baseurl-input"
            @update:value="patch((p) => (p.baseUrl = $event))"
            @blur="emit('preview')"
          />
        </div>
      </div>
      <div class="setting-inset p-2 text-[11px] font-mono space-y-1 overflow-hidden">
        <div class="flex items-center justify-between gap-2 text-secondary min-w-0">
          <span class="shrink-0 mr-2">Chat 端点 (<code class="text-emerald-400">chat</code>):</span>
          <span class="text-primary select-all min-w-0 flex-1 text-right truncate" data-testid="preview-chat-endpoint">
            {{ preview?.chatEndpointUrl || chatEndpoint }}
          </span>
        </div>
        <div class="flex items-center justify-between gap-2 text-secondary min-w-0">
          <span class="shrink-0 mr-2">Responses 端点 (<code class="text-blue-400">resp</code>):</span>
          <span class="text-primary select-all min-w-0 flex-1 text-right truncate" data-testid="preview-resp-endpoint">
            {{ preview?.respEndpointUrl || respEndpoint }}
          </span>
        </div>
      </div>
      <div class="space-y-3">
        <div class="space-y-1" data-testid="provider-apikey-block">
          <div class="flex items-center justify-between">
            <label class="text-xs text-secondary">API Key 密钥 (Windows DPAPI 加密存储)</label>
            <n-tag
              :type="provider.hasApiKey || provider.apiKeyMasked ? 'success' : 'warning'"
              size="tiny"
              :bordered="false"
              class="font-mono"
            >
              {{ provider.apiKeyMasked ? `已加密 (${provider.apiKeyMasked})` : '未设置密钥' }}
            </n-tag>
          </div>
          <n-input
            :value="apiKey"
            type="password"
            show-password-on="click"
            :placeholder="
              provider.apiKeyMasked
                ? `当前已保存 ${provider.apiKeyMasked}，输入新密钥可覆盖`
                : '输入 sk-... 密钥（保存时自动经 DPAPI 加密落盘，绝不明文落盘）'
            "
            size="small"
            class="font-mono text-xs"
            data-testid="provider-apikey-input"
            @update:value="emit('update:api-key', $event)"
          />
        </div>
        <div class="space-y-1" data-testid="provider-proxy-block">
          <label class="text-xs text-secondary">服务商代理策略 (默认继承全局)</label>
          <n-select
            :value="provider.proxyMode"
            :options="providerProxyModeOptions"
            size="small"
            data-testid="provider-proxy-mode-select"
            @update:value="onProxyMode($event)"
          />
          <div class="text-[11px] text-secondary font-mono">当前继承指向：{{ proxySummary }}</div>
        </div>
      </div>
      <div v-if="provider.proxyMode === 'Custom' && provider.customProxy" class="setting-inset p-2.5 grid grid-cols-12 gap-2.5 min-w-0">
        <div class="col-span-3 space-y-1 min-w-0">
          <label class="text-[11px] text-secondary">协议</label>
          <n-select
            :value="provider.customProxy.scheme"
            :options="proxySchemeOptions"
            size="tiny"
            @update:value="patch((p) => (p.customProxy!.scheme = $event))"
          />
        </div>
        <div class="col-span-6 space-y-1 min-w-0">
          <label class="text-[11px] text-secondary">独立代理主机</label>
          <n-input
            :value="provider.customProxy.host"
            placeholder="127.0.0.1"
            size="tiny"
            class="font-mono text-xs"
            @update:value="patch((p) => (p.customProxy!.host = $event))"
          />
        </div>
        <div class="col-span-3 space-y-1 min-w-0">
          <label class="text-[11px] text-secondary">端口</label>
          <n-input-number
            :value="provider.customProxy.port"
            :min="1"
            :max="65535"
            size="tiny"
            :show-button="false"
            class="font-mono text-xs w-full max-w-full"
            @update:value="patch((p) => (p.customProxy!.port = $event ?? 7890))"
          />
        </div>
      </div>
    </div>
  </n-card>
</template>
<script setup lang="ts">
import type { ProviderConfig, ProviderEndpointPreviewDto, ProviderProxyMode } from '@shared/bridge/hostBridge';
import { providerProxyModeOptions, proxySchemeOptions } from '../composables/providerOptions';

const props = defineProps<{
  provider: ProviderConfig;
  apiKey: string;
  preview: ProviderEndpointPreviewDto | null;
  proxySummary: string;
  chatEndpoint: string;
  respEndpoint: string;
}>();
const emit = defineEmits<{
  change: [next: ProviderConfig];
  'update:api-key': [v: string];
  preview: [];
  'proxy-change': [mode: ProviderProxyMode];
}>();
function patch(fn: (p: ProviderConfig) => void): void {
  const next = JSON.parse(JSON.stringify(props.provider)) as ProviderConfig;
  fn(next);
  emit('change', next);
}
function onProxyMode(mode: ProviderProxyMode): void {
  patch((p) => {
    p.proxyMode = mode;
  });
  emit('proxy-change', mode);
}
</script>
