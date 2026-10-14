<template>
  <div class="grid grid-cols-1 lg:grid-cols-12 gap-4 min-w-0 max-w-full overflow-x-hidden" data-testid="provider-manager-panel">
    <!-- 左侧：服务商列表栏 (4 列) -->
    <div class="lg:col-span-4 flex flex-col space-y-2.5 min-w-0">
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

      <div class="space-y-2 min-w-0">
        <div
          v-for="item in configStore.providersDoc.providers"
          :key="item.id"
          class="setting-option p-3 cursor-pointer min-w-0"
          :class="{ 'setting-option-active': selectedProviderId === item.id }"
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

          <div class="mt-1.5 text-[11px] font-mono text-secondary truncate min-w-0">
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
    <div v-if="editingProvider" class="lg:col-span-8 space-y-3.5 min-w-0 max-w-full">
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
          <div class="setting-inset p-2 text-[11px] font-mono space-y-1 overflow-hidden">
            <div class="flex items-center justify-between gap-2 text-secondary min-w-0">
              <span class="shrink-0 mr-2">Chat 端点 (<code class="text-emerald-400">chat</code>):</span>
              <span class="text-primary select-all min-w-0 flex-1 text-right truncate" data-testid="preview-chat-endpoint">
                {{ endpointPreview?.chatEndpointUrl || computedChatEndpoint }}
              </span>
            </div>
            <div class="flex items-center justify-between gap-2 text-secondary min-w-0">
              <span class="shrink-0 mr-2">Responses 端点 (<code class="text-blue-400">resp</code>):</span>
              <span class="text-primary select-all min-w-0 flex-1 text-right truncate" data-testid="preview-resp-endpoint">
                {{ endpointPreview?.respEndpointUrl || computedRespEndpoint }}
              </span>
            </div>
          </div>

          <!-- API Key (DPAPI 加密保护)：独占一行 -->
          <div class="space-y-3">
            <div class="space-y-1" data-testid="provider-apikey-block">
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

            <div class="space-y-1" data-testid="provider-proxy-block">
              <label class="text-xs text-secondary">服务商代理策略 (默认继承全局)</label>
              <n-select
                v-model:value="editingProvider.proxyMode"
                :options="providerProxyModeOptions"
                size="small"
                data-testid="provider-proxy-mode-select"
                @update:value="handleProviderProxyModeChange"
              />
              <div class="text-[11px] text-secondary font-mono">当前继承指向：{{ configStore.globalProxySummaryText }}</div>
            </div>
          </div>

          <!-- 当服务商选择独立代理 (Custom) 时展开 -->
          <div
            v-if="editingProvider.proxyMode === 'Custom' && editingProvider.customProxy"
            class="setting-inset p-2.5 grid grid-cols-12 gap-2.5 min-w-0"
          >
            <div class="col-span-3 space-y-1 min-w-0">
              <label class="text-[11px] text-secondary">协议</label>
              <n-select
                v-model:value="editingProvider.customProxy.scheme"
                :options="proxySchemeOptions"
                size="tiny"
              />
            </div>
            <div class="col-span-6 space-y-1 min-w-0">
              <label class="text-[11px] text-secondary">独立代理主机</label>
              <n-input
                v-model:value="editingProvider.customProxy.host"
                placeholder="127.0.0.1"
                size="tiny"
                class="font-mono text-xs"
              />
            </div>
            <div class="col-span-3 space-y-1 min-w-0">
              <label class="text-[11px] text-secondary">端口</label>
              <n-input-number
                v-model:value="editingProvider.customProxy.port"
                :min="1"
                :max="65535"
                size="tiny"
                :show-button="false"
                class="font-mono text-xs w-full max-w-full"
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
              class="grid grid-cols-1 sm:grid-cols-12 gap-2 items-center min-w-0"
            >
              <div class="col-span-1 flex justify-center">
                <n-switch
                  v-model:value="hdr.enabled"
                  size="small"
                  @update:value="refreshEndpointPreview"
                />
              </div>
              <div class="col-span-4 min-w-0">
                <n-input
                  v-model:value="hdr.key"
                  placeholder="Header 名称，如 X-Session-Hash"
                  size="small"
                  class="font-mono text-xs w-full"
                  @blur="refreshEndpointPreview"
                />
              </div>
              <div class="col-span-5 min-w-0">
                <n-input
                  v-model:value="hdr.valueTemplate"
                  placeholder="值或模板，如 ${session-hash}"
                  size="small"
                  class="font-mono text-xs w-full"
                  @blur="refreshEndpointPreview"
                />
              </div>
              <div class="col-span-2 flex items-center justify-end gap-1 min-w-[86px]">
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
            暂无自定义请求头，可点击下方按钮添加（例如注入 <code class="font-mono">${session-hash}</code> 实现网关亲和路由；推荐同时保留 <code class="font-mono">x-opencode-session</code> 以兼容 opencode 网关）。
          </div>

          <div class="flex items-center justify-between pt-1">
            <div class="flex flex-wrap items-center gap-2">
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
              <n-button size="tiny" secondary data-testid="add-opencode-session-header-btn" @click="addOpencodeSessionHeader">
                + x-opencode-session（会话亲和）
              </n-button>
            </div>

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
            class="setting-inset p-2 text-[11px] font-mono space-y-1 min-w-0"
            data-testid="resolved-headers-preview-box"
          >
            <div class="text-secondary">实际发包 Header 注入预览：</div>
            <div
              v-for="(val, key) in endpointPreview.resolvedHeaders"
              :key="key"
              class="flex items-center justify-between gap-2 min-w-0"
            >
              <span class="text-blue-400 shrink-0">{{ key }}:</span>
              <span class="text-emerald-400 min-w-0 break-all text-right">{{ val }}</span>
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
            :key="mdl.id + '-' + mIdx"
            class="setting-option p-2.5 cursor-pointer min-w-0"
            :class="{ 'setting-option-active': expandedModelIndex === mIdx }"
            :data-testid="`model-row-${mIdx}`"
            @click="toggleModelExpand(mIdx)"
          >
            <div class="flex items-center justify-between gap-2 min-w-0">
              <div class="flex items-center gap-2 min-w-0">
                <ChevronDown class="w-3.5 h-3.5 transition-transform shrink-0" :class="{ 'rotate-180': expandedModelIndex === mIdx }" />
                <span class="text-xs font-semibold font-mono truncate">{{ mdl.id || '(未命名模型)' }}</span>
                <span v-if="mdl.displayName" class="text-[11px] text-secondary truncate">({{ mdl.displayName }})</span>
                <n-tag :type="mdl.protocol === 'resp' ? 'info' : 'success'" size="small" :bordered="false" class="font-mono shrink-0">{{ mdl.protocol === 'resp' ? 'resp' : 'chat' }}</n-tag>
              </div>
              <div class="flex items-center gap-2 shrink-0" @click.stop>
                <span class="text-[11px] font-mono text-secondary hidden sm:inline">{{ formatK(mdl.maxContextTokens) }} / Out {{ formatK(mdl.maxOutputTokens) }}</span>
                <n-tag v-if="isModelActive(editingProvider.id, mdl.id)" type="primary" size="small" :bordered="false">默认</n-tag>
                <n-button size="tiny" secondary @click="handleSetActiveModel(editingProvider.id, mdl.id)">设为默认</n-button>
              </div>
            </div>
            <n-collapse-transition :show="expandedModelIndex === mIdx">
              <div class="pt-2.5 mt-2 border-t border-subtle/60 space-y-2.5" @click.stop>
                <div class="grid grid-cols-12 gap-2 min-w-0">
                  <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                    <label class="text-[11px] text-secondary">模型 ID (必填)</label>
                    <n-input v-model:value="mdl.id" placeholder="如 gpt-4o / o3-mini" size="small" class="font-mono text-xs" />
                  </div>
                  <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                    <label class="text-[11px] text-secondary">显示名称</label>
                    <n-input v-model:value="mdl.displayName" placeholder="显示别名（可选）" size="small" class="text-xs" />
                  </div>
                  <div class="col-span-8 sm:col-span-4 space-y-1 min-w-0">
                    <label class="text-[11px] text-secondary">通信协议</label>
                    <n-select v-model:value="mdl.protocol" :options="protocolOptions" size="small" class="font-mono text-xs" />
                  </div>
                  <div class="col-span-4 sm:col-span-2 space-y-1 min-w-0">
                    <label class="text-[11px] text-secondary">启用</label>
                    <div class="flex items-center gap-1.5 h-[34px]">
                      <n-switch v-model:value="mdl.enabled" size="small" />
                      <span class="text-[11px] text-secondary">{{ mdl.enabled ? '启用' : '停用' }}</span>
                    </div>
                  </div>
                  <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                    <label class="text-[11px] text-secondary">温度 (部分推理模型需留空)</label>
                    <div class="flex items-center gap-2">
                      <n-input-number
                        v-model:value="mdl.temperature"
                        :min="0"
                        :max="2"
                        :step="0.1"
                        size="small"
                        class="font-mono text-xs w-full max-w-full"
                        placeholder="留空则不下发"
                      />
                      <n-button size="tiny" quaternary @click="mdl.temperature = null">清空（留空）</n-button>
                    </div>
                  </div>
                  <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                    <label class="text-[11px] text-secondary">最大上下文 Token</label>
                    <n-input-number
                      v-model:value="mdl.maxContextTokens"
                      :min="1024"
                      :max="2000000"
                      :step="4096"
                      size="small"
                      class="font-mono text-xs w-full max-w-full"
                    />
                    <div class="flex flex-wrap items-center gap-1.5 pt-1">
                      <n-button
                        v-for="p in CONTEXT_PRESETS"
                        :key="p.value"
                        size="tiny"
                        secondary
                        :type="mdl.maxContextTokens === p.value ? 'primary' : 'default'"
                        @click="setPreset(mdl, 'maxContextTokens', p.value)"
                      >{{ p.label }}</n-button>
                    </div>
                  </div>
                  <div class="col-span-12 sm:col-span-6 space-y-1 min-w-0">
                    <label class="text-[11px] text-secondary">最大输出 Token</label>
                    <n-input-number
                      v-model:value="mdl.maxOutputTokens"
                      :min="256"
                      :max="256000"
                      :step="1024"
                      size="small"
                      class="font-mono text-xs w-full max-w-full"
                    />
                    <div class="flex flex-wrap items-center gap-1.5 pt-1">
                      <n-button
                        v-for="p in OUTPUT_PRESETS"
                        :key="p.value"
                        size="tiny"
                        secondary
                        :type="mdl.maxOutputTokens === p.value ? 'primary' : 'default'"
                        @click="setPreset(mdl, 'maxOutputTokens', p.value)"
                      >{{ p.label }}</n-button>
                    </div>
                  </div>
                </div>
                <div class="text-[11px] font-mono text-secondary break-all min-w-0">
                  {{ mdl.protocol === 'resp' ? 'resp → ' : 'chat → ' }}{{ mdl.protocol === 'resp' ? computedRespEndpoint : computedChatEndpoint }}
                </div>
                <div class="flex flex-wrap items-center justify-between gap-2 pt-1">
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
                  <n-button quaternary size="tiny" type="error" :disabled="editingProvider.models.length <= 1" @click="removeModelRow(mIdx)">
                    <template #icon>
                      <Trash2 class="w-3.5 h-3.5" />
                    </template>
                    删除模型
                  </n-button>
                </div>
              </div>
            </n-collapse-transition>
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
  ChevronDown,
} from 'lucide-vue-next';
import { useConfigStore } from '@/stores/configStore';
import type {
  ModelConfig,
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

const expandedModelIndex = ref<number | null>(null);

const CONTEXT_PRESETS = [
  { label: '128k', value: 128000 },
  { label: '256k', value: 256000 },
  { label: '512k', value: 512000 },
  { label: '1M', value: 1000000 },
];

const OUTPUT_PRESETS = [
  { label: '4k', value: 4096 },
  { label: '8k', value: 8192 },
  { label: '16k', value: 16384 },
  { label: '50k', value: 51200 },
  { label: '64k', value: 65536 },
  { label: '128k', value: 131072 },
];

function toggleModelExpand(idx: number) {
  expandedModelIndex.value = expandedModelIndex.value === idx ? null : idx;
}

function formatK(v: number | null | undefined): string {
  if (v == null || Number.isNaN(v)) return '-';
  if (v >= 1048576) {
    const m = v / 1048576;
    return `${Number.isInteger(m) ? m : m.toFixed(1)}M`;
  }
  return `${Math.round(v / 1024)}K`;
}

function setPreset(mdl: ModelConfig, field: 'maxContextTokens' | 'maxOutputTokens', value: number) {
  mdl[field] = value;
}

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
    expandedModelIndex.value = null;
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

watch(
  () => editingProvider.value?.id,
  () => {
    expandedModelIndex.value = null;
  }
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

function addOpencodeSessionHeader() {
  if (!editingProvider.value) return;
  editingProvider.value.enableCustomHeaders = true;
  const exists = editingProvider.value.customHeaders.some((h) => h.key.toLowerCase() === 'x-opencode-session');
  if (!exists) {
    editingProvider.value.customHeaders.push({
      key: 'x-opencode-session',
      valueTemplate: '${session-hash}',
      enabled: true,
      isSecret: false,
    });
  }
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
  expandedModelIndex.value = editingProvider.value.models.length - 1;
}

function removeModelRow(index: number) {
  if (!editingProvider.value || editingProvider.value.models.length <= 1) return;
  editingProvider.value.models.splice(index, 1);
  expandedModelIndex.value = null;
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
      {
        key: 'x-opencode-session',
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
    expandedModelIndex.value = null;
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
