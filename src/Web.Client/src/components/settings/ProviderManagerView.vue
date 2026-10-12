<template>
  <div class="grid grid-cols-12 gap-4" data-testid="provider-manager-panel">
    <!-- 左侧：服务商列表栏 (4 列) -->
    <div class="col-span-4 flex flex-col space-y-2.5">
      <div class="flex items-center justify-between px-1">
        <span class="text-xs font-semibold text-secondary uppercase tracking-wider">
          已配置服务商 ({{ configStore.providersDoc.providers.length }})
        </span>
        <n-button
          type="primary"
          size="tiny"
          secondary
          data-testid="add-provider-btn"
          @click="handleAddNewProvider"
        >
          <template #icon>
            <Plus class="w-3.5 h-3.5" />
          </template>
          新增服务商
        </n-button>
      </div>

      <div class="space-y-2 max-h-[540px] overflow-y-auto pr-1">
        <div
          v-for="item in configStore.providersDoc.providers"
          :key="item.id"
          class="p-3 rounded-lg border cursor-pointer transition-colors"
          :class="
            selectedProviderId === item.id
              ? 'border-blue-500/60 bg-blue-500/10'
              : 'border-subtle bg-black/15 hover:bg-white/[0.03]'
          "
          :data-testid="`provider-list-item-${item.id}`"
          @click="selectProvider(item.id)"
        >
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-2 min-w-0">
              <Server class="w-4 h-4 text-blue-400 shrink-0" />
              <span class="text-xs font-semibold truncate">{{ item.name }}</span>
            </div>
            <div class="flex items-center gap-1 shrink-0">
              <n-tag
                v-if="configStore.providersDoc.activeProviderId === item.id"
                type="info"
                size="small"
                :bordered="false"
              >
                当前激活
              </n-tag>
              <n-tag
                :type="item.enabled ? 'success' : 'default'"
                size="small"
                :bordered="false"
              >
                {{ item.enabled ? '启用' : '停用' }}
              </n-tag>
            </div>
          </div>

          <div class="mt-1.5 text-[11px] font-mono text-secondary truncate">
            {{ item.baseUrl }}
          </div>

          <div class="mt-2 flex items-center justify-between text-[11px] text-secondary">
            <span>{{ item.models.length }} 个模型</span>
            <span class="font-mono">
              代理:
              {{
                item.proxyMode === 'Inherit'
                  ? '继承全局'
                  : item.proxyMode === 'None'
                  ? '强制直连'
                  : '独立代理'
              }}
            </span>
          </div>
        </div>
      </div>
    </div>

    <!-- 右侧：当前选中服务商详细配置面板 (8 列) -->
    <div v-if="editingProvider" class="col-span-8 space-y-3.5 max-h-[580px] overflow-y-auto pr-1">
      <!-- 1. 基础连接、DPAPI 密钥与代理策略卡片 -->
      <n-card size="small" title="服务商网关、密钥与代理策略">
        <template #header-extra>
          <div class="flex items-center gap-2">
            <span class="text-xs text-secondary">启用该服务商</span>
            <n-switch v-model:value="editingProvider.enabled" size="small" />
          </div>
        </template>

        <div class="space-y-3">
          <div class="grid grid-cols-12 gap-3">
            <div class="col-span-5 space-y-1">
              <label class="text-xs text-secondary">服务商名称 (Name)</label>
              <n-input
                v-model:value="editingProvider.name"
                placeholder="例如 OpenAI / DeepSeek / 企业网关"
                size="small"
                data-testid="provider-name-input"
              />
            </div>

            <div class="col-span-7 space-y-1">
              <div class="flex items-center justify-between">
                <label class="text-xs text-secondary">
                  API Base URL (末尾加 <code class="font-mono text-amber-300">#</code> 可禁用自动补齐 /v1)
                </label>
              </div>
              <n-input
                v-model:value="editingProvider.baseUrl"
                placeholder="https://api.openai.com/v1"
                size="small"
                class="font-mono text-xs"
                data-testid="provider-baseurl-input"
                @blur="refreshEndpointPreview"
              />
            </div>
          </div>

          <!-- 实时端点归一化预览条 -->
          <div class="p-2 rounded bg-black/20 border border-subtle text-[11px] font-mono space-y-1">
            <div class="flex items-center justify-between text-secondary">
              <span>Chat 端点 (<code class="text-emerald-400">chat</code>):</span>
              <span class="text-primary select-all" data-testid="preview-chat-endpoint">
                {{ endpointPreview?.chatEndpointUrl || computedChatEndpoint }}
              </span>
            </div>
            <div class="flex items-center justify-between text-secondary">
              <span>Responses 端点 (<code class="text-blue-400">resp</code>):</span>
              <span class="text-primary select-all" data-testid="preview-resp-endpoint">
                {{ endpointPreview?.respEndpointUrl || computedRespEndpoint }}
              </span>
            </div>
          </div>

          <!-- API Key (DPAPI 加密保护) 与代理策略 -->
          <div class="grid grid-cols-12 gap-3">
            <div class="col-span-7 space-y-1">
              <div class="flex items-center justify-between">
                <label class="text-xs text-secondary">API Key 密钥 (Windows DPAPI 加密存储)</label>
                <n-tag
                  :type="editingProvider.hasApiKey || editingProvider.apiKeyMasked ? 'success' : 'warning'"
                  size="tiny"
                  :bordered="false"
                  class="font-mono"
                >
                  {{
                    editingProvider.apiKeyMasked
                      ? `已加密 (${editingProvider.apiKeyMasked})`
                      : '未设置密钥'
                  }}
                </n-tag>
              </div>
              <n-input
                v-model:value="apiKeyInput"
                type="password"
                show-password-on="click"
                :placeholder="
                  editingProvider.apiKeyMasked
                    ? `当前已保存 ${editingProvider.apiKeyMasked}，输入新密钥可覆盖`
                    : '输入 sk-... 密钥（保存时自动经 DPAPI 加密落盘，绝不明文落盘）'
                "
                size="small"
                class="font-mono text-xs"
                data-testid="provider-apikey-input"
              />
            </div>

            <div class="col-span-5 space-y-1">
              <label class="text-xs text-secondary">服务商代理策略 (默认继承全局)</label>
              <n-select
                v-model:value="editingProvider.proxyMode"
                :options="providerProxyModeOptions"
                size="small"
                data-testid="provider-proxy-mode-select"
                @update:value="handleProviderProxyModeChange"
              />
            </div>
          </div>

          <!-- 当服务商选择独立代理 (Custom) 时展开 -->
          <div
            v-if="editingProvider.proxyMode === 'Custom' && editingProvider.customProxy"
            class="p-2.5 rounded border border-subtle bg-black/15 grid grid-cols-12 gap-2.5"
          >
            <div class="col-span-3 space-y-1">
              <label class="text-[11px] text-secondary">协议</label>
              <n-select
                v-model:value="editingProvider.customProxy.scheme"
                :options="proxySchemeOptions"
                size="tiny"
              />
            </div>
            <div class="col-span-6 space-y-1">
              <label class="text-[11px] text-secondary">独立代理主机</label>
              <n-input
                v-model:value="editingProvider.customProxy.host"
                placeholder="127.0.0.1"
                size="tiny"
                class="font-mono text-xs"
              />
            </div>
            <div class="col-span-3 space-y-1">
              <label class="text-[11px] text-secondary">端口</label>
              <n-input-number
                v-model:value="editingProvider.customProxy.port"
                :min="1"
                :max="65535"
                size="tiny"
                class="font-mono text-xs w-full"
              />
            </div>
          </div>
        </div>
      </n-card>

      <!-- 2. 自定义请求头与 ${session-hash} 预设变量引擎卡片 -->
      <n-card size="small" title="自定义请求头与预设变量引擎">
        <template #header-extra>
          <div class="flex items-center gap-2">
            <span class="text-xs text-secondary">启用自定义请求头</span>
            <n-switch
              v-model:value="editingProvider.enableCustomHeaders"
              size="small"
              data-testid="enable-custom-headers-switch"
              @update:value="refreshEndpointPreview"
            />
          </div>
        </template>

        <div class="space-y-3">
          <!-- 预设变量快捷插入胶囊 -->
          <div class="flex flex-wrap items-center gap-1.5">
            <span class="text-[11px] text-secondary mr-1">点击插入预设变量：</span>
            <n-tag
              v-for="v in presetVariables"
              :key="v.token"
              size="small"
              :bordered="false"
              type="info"
              class="cursor-pointer font-mono hover:opacity-80"
              :title="v.desc"
              @click="insertVariableToLastHeader(v.token)"
            >
              {{ v.token }}
            </n-tag>
          </div>

          <!-- Header 列表 -->
          <div v-if="editingProvider.customHeaders.length > 0" class="space-y-2">
            <div
              v-for="(hdr, idx) in editingProvider.customHeaders"
              :key="idx"
              class="grid grid-cols-12 gap-2 items-center"
            >
              <div class="col-span-1 flex justify-center">
                <n-switch
                  v-model:value="hdr.enabled"
                  size="small"
                  @update:value="refreshEndpointPreview"
                />
              </div>
              <div class="col-span-4">
                <n-input
                  v-model:value="hdr.key"
                  placeholder="Header 名称，如 X-Session-Hash"
                  size="small"
                  class="font-mono text-xs"
                  @blur="refreshEndpointPreview"
                />
              </div>
              <div class="col-span-5">
                <n-input
                  v-model:value="hdr.valueTemplate"
                  placeholder="值或模板，如 ${session-hash}"
                  size="small"
                  class="font-mono text-xs"
                  @blur="refreshEndpointPreview"
                />
              </div>
              <div class="col-span-2 flex items-center justify-end gap-1.5">
                <n-checkbox v-model:checked="hdr.isSecret" size="small" title="日志脱敏">
                  <span class="text-[11px] text-secondary">敏感</span>
                </n-checkbox>
                <n-button
                  quaternary
                  size="tiny"
                  type="error"
                  @click="removeCustomHeader(idx)"
                >
                  <template #icon>
                    <Trash2 class="w-3.5 h-3.5" />
                  </template>
                </n-button>
              </div>
            </div>
          </div>
          <div v-else class="text-xs text-secondary py-1">
            暂无自定义请求头，可点击下方按钮添加（例如注入 <code class="font-mono">${session-hash}</code> 实现网关亲和路由）。
          </div>

          <div class="flex items-center justify-between pt-1">
            <n-button
              size="tiny"
              secondary
              data-testid="add-custom-header-btn"
              @click="addCustomHeader"
            >
              <template #icon>
                <Plus class="w-3.5 h-3.5" />
              </template>
              添加自定义 Header
            </n-button>

            <span
              v-if="endpointPreview?.sessionHash"
              class="text-[11px] font-mono text-secondary"
              data-testid="preview-session-hash"
            >
              当前会话 ${session-hash} 预览:
              <strong class="text-emerald-400">{{ endpointPreview.sessionHash }}</strong>
            </span>
          </div>

          <!-- 实时解析结果预览 -->
          <div
            v-if="
              editingProvider.enableCustomHeaders &&
              endpointPreview &&
              Object.keys(endpointPreview.resolvedHeaders).length > 0
            "
            class="p-2 rounded bg-black/20 border border-subtle text-[11px] font-mono space-y-1"
            data-testid="resolved-headers-preview-box"
          >
            <div class="text-secondary">实际发包 Header 注入预览：</div>
            <div
              v-for="(val, key) in endpointPreview.resolvedHeaders"
              :key="key"
              class="flex items-center justify-between"
            >
              <span class="text-blue-400">{{ key }}:</span>
              <span class="text-emerald-400">{{ val }}</span>
            </div>
          </div>
        </div>
      </n-card>

      <!-- 3. 模型配置列表卡片 (Protocol: chat / resp, MaxContextTokens, MaxOutputTokens) -->
      <n-card size="small" title="模型列表与协议预算配置 (Models)">
        <template #header-extra>
          <n-button
            size="tiny"
            type="primary"
            secondary
            data-testid="add-model-btn"
            @click="addModelRow"
          >
            <template #icon>
              <Plus class="w-3.5 h-3.5" />
            </template>
            添加模型
          </n-button>
        </template>

        <div class="space-y-2.5">
          <div
            v-for="(mdl, mIdx) in editingProvider.models"
            :key="mIdx"
            class="p-2.5 rounded-lg border border-subtle bg-black/15 space-y-2"
            :data-testid="`model-row-${mIdx}`"
          >
            <div class="grid grid-cols-12 gap-2 items-center">
              <div class="col-span-3 space-y-1">
                <label class="text-[11px] text-secondary">模型 ID (必填)</label>
                <n-input
                  v-model:value="mdl.id"
                  placeholder="如 gpt-4o / o3-mini"
                  size="small"
                  class="font-mono text-xs"
                />
              </div>

              <div class="col-span-3 space-y-1">
                <label class="text-[11px] text-secondary">显示名称</label>
                <n-input
                  v-model:value="mdl.displayName"
                  placeholder="显示别名（可选）"
                  size="small"
                  class="text-xs"
                />
              </div>

              <div class="col-span-2 space-y-1">
                <label class="text-[11px] text-secondary">通信协议</label>
                <n-select
                  v-model:value="mdl.protocol"
                  :options="protocolOptions"
                  size="small"
                  class="font-mono text-xs"
                />
              </div>

              <div class="col-span-2 space-y-1">
                <label class="text-[11px] text-secondary">最大上下文 Token</label>
                <n-input-number
                  v-model:value="mdl.maxContextTokens"
                  :min="1024"
                  :max="2000000"
                  :step="4096"
                  size="small"
                  class="font-mono text-xs w-full"
                />
              </div>

              <div class="col-span-2 space-y-1">
                <label class="text-[11px] text-secondary">最大输出 Token</label>
                <n-input-number
                  v-model:value="mdl.maxOutputTokens"
                  :min="256"
                  :max="256000"
                  :step="1024"
                  size="small"
                  class="font-mono text-xs w-full"
                />
              </div>
            </div>

            <div class="flex items-center justify-between pt-1 border-t border-subtle/60 text-xs">
              <div class="flex items-center gap-3">
                <n-tag
                  :type="mdl.protocol === 'resp' ? 'info' : 'success'"
                  size="small"
                  :bordered="false"
                  class="font-mono"
                >
                  {{ mdl.protocol === 'resp' ? 'resp (/v1/responses)' : 'chat (/v1/chat/completions)' }}
                </n-tag>
                <span class="text-[11px] font-mono text-secondary">
                  Context: {{ mdl.maxContextTokens.toLocaleString() }} · MaxOutput:
                  {{ mdl.maxOutputTokens.toLocaleString() }}
                </span>
              </div>

              <div class="flex items-center gap-2">
                <n-button
                  size="tiny"
                  :type="isModelActive(editingProvider.id, mdl.id) ? 'primary' : 'default'"
                  secondary
                  @click="handleSetActiveModel(editingProvider.id, mdl.id)"
                >
                  <template #icon>
                    <CheckCircle2 class="w-3.5 h-3.5" />
                  </template>
                  {{ isModelActive(editingProvider.id, mdl.id) ? '当前默认模型' : '设为默认模型' }}
                </n-button>

                <n-button
                  quaternary
                  size="tiny"
                  type="error"
                  :disabled="editingProvider.models.length <= 1"
                  @click="removeModelRow(mIdx)"
                >
                  <template #icon>
                    <Trash2 class="w-3.5 h-3.5" />
                  </template>
                </n-button>
              </div>
            </div>
          </div>
        </div>
      </n-card>

      <!-- 底部操作与连通测试结果区 -->
      <div class="p-3 rounded-lg border border-subtle bg-surface space-y-2.5">
        <div class="flex items-center justify-between">
          <n-button
            size="small"
            type="error"
            ghost
            :disabled="configStore.providersDoc.providers.length <= 1"
            @click="handleDeleteCurrentProvider"
          >
            <template #icon>
              <Trash2 class="w-3.5 h-3.5" />
            </template>
            删除该服务商
          </n-button>

          <n-space :size="10">
            <n-button
              size="small"
              secondary
              :loading="testingProvider"
              data-testid="test-provider-btn"
              @click="handleTestCurrentProvider"
            >
              <template #icon>
                <Activity class="w-3.5 h-3.5" />
              </template>
              测试该服务商网关连通性
            </n-button>

            <n-button
              type="primary"
              size="small"
              :loading="configStore.savingProvider"
              data-testid="save-provider-btn"
              @click="handleSaveCurrentProvider"
            >
              <template #icon>
                <Save class="w-3.5 h-3.5" />
              </template>
              保存服务商与模型配置
            </n-button>
          </n-space>
        </div>

        <div
          v-if="providerTestResult"
          class="p-2.5 rounded border text-xs font-mono flex items-center justify-between"
          :class="
            providerTestResult.success
              ? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-300'
              : 'border-rose-500/40 bg-rose-500/10 text-rose-300'
          "
        >
          <span>
            [{{ providerTestResult.diagnosticCategory }}]
            {{
              providerTestResult.success
                ? `网关连通正常 (HTTP ${providerTestResult.statusCode})`
                : providerTestResult.errorMessage || '网关连接失败'
            }}
          </span>
          <span>
            代理: {{ providerTestResult.effectiveProxyUsed }} · {{ providerTestResult.elapsedMs }} ms
          </span>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { useMessage } from 'naive-ui';
import {
  Plus,
  Server,
  Trash2,
  CheckCircle2,
  Activity,
  Save,
} from 'lucide-vue-next';
import { useConfigStore } from '@/stores/configStore';
import type {
  ProviderConfig,
  ProviderEndpointPreviewDto,
  ProxyConnectivityTestResult,
  ProviderProxyMode,
} from '@/bridge/hostBridge';

const configStore = useConfigStore();
const message = useMessage();

const selectedProviderId = ref<string>('');
const editingProvider = ref<ProviderConfig | null>(null);
const apiKeyInput = ref<string>('');
const endpointPreview = ref<ProviderEndpointPreviewDto | null>(null);
const testingProvider = ref(false);
const providerTestResult = ref<ProxyConnectivityTestResult | null>(null);

const providerProxyModeOptions = [
  { label: '继承全局代理设置 (Inherit)', value: 'Inherit' },
  { label: '不使用代理 / 强制直连 (None)', value: 'None' },
  { label: '使用独立自定义代理 (Custom)', value: 'Custom' },
];

const proxySchemeOptions = [
  { label: 'HTTP', value: 'http' },
  { label: 'HTTPS', value: 'https' },
  { label: 'SOCKS5', value: 'socks5' },
];

const protocolOptions = [
  { label: 'chat (/v1/chat/completions)', value: 'chat' },
  { label: 'resp (/v1/responses)', value: 'resp' },
];

const presetVariables = [
  { token: '${session-hash}', desc: '当前会话 16 位确定性 SHA-256 哈希' },
  { token: '${session-id}', desc: '当前会话原始 ID' },
  { token: '${workspace-name}', desc: '当前工作区文件夹名' },
  { token: '${workspace-hash}', desc: '当前工作区路径 16 位确定性哈希' },
  { token: '${model-id}', desc: '当前调用的模型 ID' },
  { token: '${provider-name}', desc: '当前服务商名称' },
  { token: '${request-id}', desc: '单次 HTTP 请求唯一 UUID' },
  { token: '${timestamp}', desc: '当前 UTC Unix 秒级时间戳' },
];

const computedVersionPrefix = computed(() => {
  const raw = (editingProvider.value?.baseUrl || 'https://api.openai.com/v1').trim();
  const withScheme = raw.includes('://') ? raw : `https://${raw}`;
  if (withScheme.endsWith('#')) {
    return withScheme.slice(0, -1).replace(/\/+$/, '');
  }
  const clean = withScheme.replace(/\/+$/, '');
  if (clean.toLowerCase().endsWith('/v1')) {
    return clean;
  }
  return `${clean}/v1`;
});

const computedChatEndpoint = computed(() => `${computedVersionPrefix.value}/chat/completions`);
const computedRespEndpoint = computed(() => `${computedVersionPrefix.value}/responses`);

function selectProvider(providerId: string) {
  selectedProviderId.value = providerId;
  const target = configStore.providersDoc.providers.find((p) => p.id === providerId);
  if (target) {
    editingProvider.value = JSON.parse(JSON.stringify(target));
    apiKeyInput.value = '';
    providerTestResult.value = null;
    refreshEndpointPreview();
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
      selectProvider(configStore.providersDoc.activeProviderId || list[0].id);
    } else {
      const current = list.find((p) => p.id === selectedProviderId.value);
      if (current) {
        editingProvider.value = JSON.parse(JSON.stringify(current));
      }
    }
  },
  { immediate: true, deep: true }
);

onMounted(() => {
  refreshEndpointPreview();
});

async function refreshEndpointPreview() {
  if (!editingProvider.value) return;
  const res = await configStore.previewProviderResolution(
    editingProvider.value,
    editingProvider.value.models[0]?.id || 'gpt-4o'
  );
  if (res.ok && res.data) {
    endpointPreview.value = res.data;
  }
}

function handleProviderProxyModeChange(mode: ProviderProxyMode) {
  if (!editingProvider.value) return;
  if (mode === 'Custom' && !editingProvider.value.customProxy) {
    editingProvider.value.customProxy = {
      scheme: 'http',
      host: '127.0.0.1',
      port: 7890,
      requiresAuth: false,
      bypassList: 'localhost;127.*',
      bypassOnLocal: true,
    };
  }
  refreshEndpointPreview();
}

function addCustomHeader() {
  if (!editingProvider.value) return;
  editingProvider.value.enableCustomHeaders = true;
  editingProvider.value.customHeaders.push({
    key: 'X-Session-Hash',
    valueTemplate: '${session-hash}',
    enabled: true,
    isSecret: false,
  });
  refreshEndpointPreview();
}

function removeCustomHeader(index: number) {
  if (!editingProvider.value) return;
  editingProvider.value.customHeaders.splice(index, 1);
  refreshEndpointPreview();
}

function insertVariableToLastHeader(token: string) {
  if (!editingProvider.value) return;
  editingProvider.value.enableCustomHeaders = true;
  if (editingProvider.value.customHeaders.length === 0) {
    editingProvider.value.customHeaders.push({
      key: 'X-Custom-Trace',
      valueTemplate: token,
      enabled: true,
      isSecret: false,
    });
  } else {
    const last =
      editingProvider.value.customHeaders[editingProvider.value.customHeaders.length - 1];
    last.valueTemplate = `${last.valueTemplate || ''}${token}`;
  }
  refreshEndpointPreview();
}

function addModelRow() {
  if (!editingProvider.value) return;
  editingProvider.value.models.push({
    id: `model-${editingProvider.value.models.length + 1}`,
    displayName: '',
    protocol: 'chat',
    maxContextTokens: 128000,
    maxOutputTokens: 4096,
    temperature: 0.2,
    enabled: true,
  });
}

function removeModelRow(index: number) {
  if (!editingProvider.value || editingProvider.value.models.length <= 1) return;
  editingProvider.value.models.splice(index, 1);
}

function isModelActive(providerId: string, modelId: string): boolean {
  return (
    configStore.providersDoc.activeProviderId === providerId &&
    configStore.providersDoc.activeModelId === modelId
  );
}

async function handleSetActiveModel(providerId: string, modelId: string) {
  if (!modelId.trim()) {
    message.warning('请先填写有效的模型 ID');
    return;
  }
  await handleSaveCurrentProvider(false);
  const res = await configStore.setActiveModel(providerId, modelId);
  if (res.ok) {
    message.success(`已将 ${modelId} 设为当前默认调用模型`);
  }
}

function handleAddNewProvider() {
  const newId = `provider-${Date.now().toString(36)}`;
  const newProvider: ProviderConfig = {
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
      {
        key: 'X-Session-Hash',
        valueTemplate: '${session-hash}',
        enabled: true,
        isSecret: false,
      },
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
      },
    ],
  };

  configStore.upsertProvider(newProvider).then((res) => {
    if (res.ok) {
      selectProvider(newId);
      message.success('已创建新服务商，请配置 BaseURL 与 API Key');
    }
  });
}

async function handleSaveCurrentProvider(showToast = true) {
  if (!editingProvider.value) return;
  if (!editingProvider.value.name.trim()) {
    message.warning('服务商名称不能为空');
    return;
  }

  const payload: ProviderConfig = JSON.parse(JSON.stringify(editingProvider.value));
  if (apiKeyInput.value.trim()) {
    payload.apiKey = apiKeyInput.value.trim();
  }

  const res = await configStore.upsertProvider(payload);
  if (res.ok) {
    apiKeyInput.value = '';
    await refreshEndpointPreview();
    if (showToast) {
      message.success(res.message || '服务商配置已通过 DPAPI 加密落盘');
    }
  } else {
    message.error(res.message || '保存服务商配置失败');
  }
}

async function handleDeleteCurrentProvider() {
  if (!editingProvider.value) return;
  const idToDelete = editingProvider.value.id;
  const res = await configStore.deleteProvider(idToDelete);
  if (res.ok) {
    message.success('服务商已删除');
  } else {
    message.error(res.message || '删除服务商失败');
  }
}

async function handleTestCurrentProvider() {
  if (!editingProvider.value) return;
  testingProvider.value = true;
  providerTestResult.value = null;
  try {
    const payload: ProviderConfig = JSON.parse(JSON.stringify(editingProvider.value));
    if (apiKeyInput.value.trim()) {
      payload.apiKey = apiKeyInput.value.trim();
    }
    const res = await configStore.testProviderConnectivity(payload);
    if (res.data) {
      providerTestResult.value = res.data;
    }
    if (res.ok && res.data?.success) {
      message.success(`服务商网关连通正常 (${res.data.elapsedMs} ms)`);
    } else {
      message.warning(res.data?.errorMessage || res.message || '服务商网关连接异常');
    }
  } finally {
    testingProvider.value = false;
  }
}
</script>
