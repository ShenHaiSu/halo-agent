<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden" data-testid="network-proxy-settings-panel">
    <!-- 1. 全局代理路由模式选择卡片 -->
    <n-card title="全局网络代理中枢 (ProxyHub)" size="small">
      <template #header-extra>
        <n-tag
          :type="draft.mode === 'None' ? 'warning' : draft.mode === 'Custom' ? 'info' : 'success'"
          size="small"
          :bordered="false"
          class="font-mono"
        >
          当前生效：{{ configStore.globalProxySummaryText }}
        </n-tag>
      </template>

      <div class="space-y-4">
        <n-radio-group
          v-model:value="draft.mode"
          name="global-proxy-mode"
          data-testid="global-proxy-mode-group"
        >
          <div class="grid grid-cols-1 xl:grid-cols-3 gap-3 min-w-0">
            <!-- 模式 A: 系统代理 -->
            <div
              class="setting-option p-3 cursor-pointer min-w-0"
              :class="{ 'setting-option-active': draft.mode === 'System' }"
              data-testid="proxy-mode-system"
              @click="draft.mode = 'System'"
            >
              <div class="flex items-center justify-between mb-1">
                <n-radio value="System">
                  <span class="text-xs font-semibold">自动检测系统代理</span>
                </n-radio>
                <Globe class="w-4 h-4 text-blue-400" />
              </div>
              <div class="text-[11px] text-secondary leading-relaxed pl-6">
                实时读取 Windows 注册表 WinINet / PAC 代理（自适应 Clash / v2rayN 系统代理开关）
              </div>
            </div>

            <!-- 模式 B: 手动代理 -->
            <div
              class="setting-option p-3 cursor-pointer min-w-0"
              :class="{ 'setting-option-active': draft.mode === 'Custom' }"
              data-testid="proxy-mode-custom"
              @click="draft.mode = 'Custom'"
            >
              <div class="flex items-center justify-between mb-1">
                <n-radio value="Custom">
                  <span class="text-xs font-semibold">手动配置固定代理</span>
                </n-radio>
                <Sliders class="w-4 h-4 text-emerald-400" />
              </div>
              <div class="text-[11px] text-secondary leading-relaxed pl-6">
                支持 HTTP / HTTPS / SOCKS5 协议、账密 DPAPI 加密认证与本地网段绕过列表
              </div>
            </div>

            <!-- 模式 C: 强制直连 -->
            <div
              class="setting-option p-3 cursor-pointer min-w-0"
              :class="{ 'setting-option-active': draft.mode === 'None' }"
              data-testid="proxy-mode-none"
              @click="draft.mode = 'None'"
            >
              <div class="flex items-center justify-between mb-1">
                <n-radio value="None">
                  <span class="text-xs font-semibold">不使用代理 (强制直连)</span>
                </n-radio>
                <ZapOff class="w-4 h-4 text-amber-400" />
              </div>
              <div class="text-[11px] text-secondary leading-relaxed pl-6">
                显式设置 UseProxy = false，彻底绕过操作系统代理直接连接上游网关
              </div>
            </div>
          </div>
        </n-radio-group>

        <!-- 系统代理实时快照面板 (Mode === System) -->
        <div
          v-if="draft.mode === 'System'"
          class="setting-inset p-3 space-y-2.5 min-w-0"
        >
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-2">
              <Radar class="w-4 h-4 text-blue-400" />
              <span class="text-xs font-medium">Windows 系统代理实时探测快照</span>
              <n-tag
                :type="sysProxy?.isEnabled ? 'success' : 'default'"
                size="small"
                :bordered="false"
              >
                {{ sysProxy?.isEnabled ? '已开启系统代理' : '系统未开启代理 (直连)' }}
              </n-tag>
            </div>
            <n-button
              size="tiny"
              secondary
              data-testid="refresh-sys-proxy-btn"
              @click="handleRefreshSystemProxy"
            >
              <template #icon>
                <RefreshCw class="w-3.5 h-3.5" />
              </template>
              重新检测系统代理
            </n-button>
          </div>

          <n-descriptions bordered :column="1" size="small" label-placement="left">
            <n-descriptions-item label="生效代理 URI">
              <span class="font-mono text-xs text-emerald-400 min-w-0 break-all">
                {{ sysProxy?.effectiveHttpProxyUri || 'DIRECT (无代理)' }}
              </span>
            </n-descriptions-item>
            <n-descriptions-item label="注册表 ProxyServer">
              <span class="font-mono text-xs min-w-0 break-all">
                {{ sysProxy?.proxyServerRaw || '(未配置)' }}
              </span>
            </n-descriptions-item>
            <n-descriptions-item label="PAC 自动脚本地址">
              <span class="font-mono text-xs min-w-0 break-all">
                {{ sysProxy?.autoConfigUrl || '(无)' }}
              </span>
            </n-descriptions-item>
            <n-descriptions-item label="绕过列表 (ProxyOverride)">
              <span class="font-mono text-xs truncate max-w-full min-w-0 break-all">
                {{ sysProxy?.bypassOverride || '(默认)' }}
              </span>
            </n-descriptions-item>
          </n-descriptions>
        </div>

        <!-- 手动代理详细表单 (Mode === Custom) -->
        <div
          v-if="draft.mode === 'Custom'"
          class="setting-inset p-3.5 space-y-3 min-w-0"
        >
          <div class="grid grid-cols-12 gap-3 min-w-0">
            <div class="col-span-3 space-y-1 min-w-0">
              <label class="text-xs text-secondary">代理协议 (Scheme)</label>
              <n-select
                v-model:value="draft.customProxy.scheme"
                :options="proxySchemeOptions"
                size="small"
                data-testid="custom-proxy-scheme"
              />
            </div>
            <div class="col-span-6 space-y-1 min-w-0">
              <label class="text-xs text-secondary">代理主机地址 (Host)</label>
              <n-input
                v-model:value="draft.customProxy.host"
                placeholder="例如 127.0.0.1 或 proxy.corp.local"
                size="small"
                class="font-mono text-xs"
                data-testid="custom-proxy-host"
              />
            </div>
            <div class="col-span-3 space-y-1 min-w-0">
              <label class="text-xs text-secondary">端口 (Port)</label>
              <n-input-number
                v-model:value="draft.customProxy.port"
                :min="1"
                :max="65535"
                size="small"
                class="font-mono text-xs w-full max-w-full"
                data-testid="custom-proxy-port"
              />
            </div>
          </div>

          <div class="grid grid-cols-12 gap-3 items-center pt-1 min-w-0">
            <div class="col-span-8 space-y-1 min-w-0">
              <label class="text-xs text-secondary">绕过代理地址列表 (分号或逗号分隔，支持 * 通配符)</label>
              <n-input
                v-model:value="draft.customProxy.bypassList"
                placeholder="localhost;127.*;10.*;192.168.*"
                size="small"
                class="font-mono text-xs w-full"
              />
            </div>
            <div class="col-span-4 flex items-center justify-between pt-4 px-2 min-w-0">
              <span class="text-xs text-secondary">本地地址自动绕过</span>
              <n-switch v-model:value="draft.customProxy.bypassOnLocal" size="small" />
            </div>
          </div>

          <!-- 代理身份认证 -->
          <div class="pt-2 border-t border-subtle space-y-2.5">
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-2">
                <Lock class="w-3.5 h-3.5 text-amber-400" />
                <span class="text-xs font-medium">启用代理服务器身份认证 (Username / Password)</span>
              </div>
              <n-switch v-model:value="draft.customProxy.requiresAuth" size="small" />
            </div>

            <div v-if="draft.customProxy.requiresAuth" class="grid grid-cols-2 gap-3">
              <div class="space-y-1">
                <label class="text-xs text-secondary">代理用户名</label>
                <n-input
                  v-model:value="draft.customProxy.username"
                  placeholder="输入代理认证用户名"
                  size="small"
                  class="font-mono text-xs"
                />
              </div>
              <div class="space-y-1">
                <label class="text-xs text-secondary">
                  代理密码 (DPAPI 加密落盘{{ draft.customProxy.passwordMasked ? ` · 已存 ${draft.customProxy.passwordMasked}` : '' }})
                </label>
                <n-input
                  v-model:value="proxyPasswordInput"
                  type="password"
                  show-password-on="click"
                  :placeholder="draft.customProxy.passwordMasked || '输入代理密码（留空则保持原密码）'"
                  size="small"
                  class="font-mono text-xs"
                />
              </div>
            </div>
          </div>
        </div>

        <!-- 强制直连说明面板 (Mode === None) -->
        <n-alert v-if="draft.mode === 'None'" type="warning" :show-icon="true">
          <template #icon>
            <ZapOff class="w-4 h-4" />
          </template>
          <div class="text-xs leading-relaxed">
            已选择<strong>不使用任何代理 (强制直连)</strong>。C# 宿主底层
            <code class="setting-inset px-1 py-0.5 font-mono break-all">SocketsHttpHandler.UseProxy = false</code>
            已激活，所有继承全局代理的 Provider 请求均直接连接目标服务器，不受系统代理影响。
          </div>
        </n-alert>

        <!-- 连接与请求超时设置 -->
        <div class="grid grid-cols-2 gap-3 pt-1">
          <div class="space-y-1">
            <label class="text-xs text-secondary">TCP 建连超时时间 (秒, 3 ~ 120)</label>
            <n-input-number
              v-model:value="draft.defaultConnectTimeoutSeconds"
              :min="3"
              :max="120"
              size="small"
              class="font-mono text-xs"
            />
          </div>
          <div class="space-y-1">
            <label class="text-xs text-secondary">长流式请求超时时间 (秒, 5 ~ 3600)</label>
            <n-input-number
              v-model:value="draft.defaultRequestTimeoutSeconds"
              :min="5"
              :max="3600"
              size="small"
              class="font-mono text-xs"
            />
          </div>
        </div>
      </div>

      <template #footer>
        <div class="flex items-center justify-between">
          <span class="text-[11px] text-secondary font-mono">
            配置文件：{{ systemStore.storageConfig?.dataRootPath || 'C:\\HaloAgentData' }}\config\network.json
          </span>
          <n-space :size="10">
            <n-button size="small" secondary @click="resetDraftFromStore">
              <template #icon>
                <RotateCcw class="w-3.5 h-3.5" />
              </template>
              重置修改
            </n-button>
            <n-button
              type="primary"
              size="small"
              :loading="configStore.savingNetwork"
              data-testid="save-network-settings-btn"
              @click="handleSaveNetworkSettings"
            >
              <template #icon>
                <Save class="w-3.5 h-3.5" />
              </template>
              保存全局网络配置
            </n-button>
          </n-space>
        </div>
      </template>
    </n-card>

    <!-- 2. 代理连通性与首字节延迟诊断卡片 -->
    <n-card title="代理链路连通性与延迟探测" size="small">
      <div class="space-y-3">
        <div class="flex items-center gap-2">
          <n-input
            v-model:value="probeTargetUrl"
            placeholder="输入探测目标 URL，例如 https://api.openai.com/v1/models"
            size="small"
            class="font-mono text-xs flex-1"
            data-testid="proxy-probe-url-input"
          />
          <n-button
            type="info"
            secondary
            size="small"
            :loading="configStore.testingProxy"
            data-testid="test-proxy-connectivity-btn"
            @click="handleTestConnectivity"
          >
            <template #icon>
              <Activity class="w-3.5 h-3.5" />
            </template>
            测试当前表单代理连通性
          </n-button>
        </div>

        <!-- 诊断结果展示 -->
        <div
          v-if="probeResult"
          class="p-3 rounded-lg border text-xs space-y-1.5"
          :class="
            probeResult.success
              ? 'border-emerald-500/40 bg-emerald-500/10'
              : 'border-rose-500/40 bg-rose-500/10'
          "
          data-testid="proxy-probe-result-box"
        >
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-2 font-medium">
              <CheckCircle2 v-if="probeResult.success" class="w-4 h-4 text-emerald-400" />
              <AlertTriangle v-else class="w-4 h-4 text-rose-400" />
              <span>
                {{ probeResult.success ? '代理与目标网关链路畅通' : '代理或目标连接异常' }}
              </span>
              <n-tag
                :type="probeResult.success ? 'success' : 'error'"
                size="small"
                :bordered="false"
                class="font-mono"
              >
                {{ probeResult.diagnosticCategory }}
              </n-tag>
            </div>
            <span class="font-mono text-xs">
              耗时: <strong>{{ probeResult.elapsedMs }} ms</strong>
              <template v-if="probeResult.statusCode"> · HTTP {{ probeResult.statusCode }}</template>
            </span>
          </div>

          <div class="text-[11px] font-mono text-secondary flex items-center justify-between">
            <span>实际使用代理路由: {{ probeResult.effectiveProxyUsed }}</span>
            <span>探测端点: {{ probeResult.targetUrl }}</span>
          </div>

          <div v-if="probeResult.errorMessage" class="text-[11px] text-rose-300 font-mono pt-0.5">
            诊断详情: {{ probeResult.errorMessage }}
          </div>
        </div>
      </div>
    </n-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { useMessage } from 'naive-ui';
import {
  Globe,
  Sliders,
  ZapOff,
  Radar,
  RefreshCw,
  Lock,
  RotateCcw,
  Save,
  Activity,
  CheckCircle2,
  AlertTriangle,
} from 'lucide-vue-next';
import { useConfigStore } from '@/stores/configStore';
import { useSystemStore } from '@/stores/systemStore';
import type { GlobalNetworkSettings } from '@/bridge/hostBridge';

const configStore = useConfigStore();
const systemStore = useSystemStore();
const message = useMessage();

const proxySchemeOptions = [
  { label: 'HTTP (http://)', value: 'http' },
  { label: 'HTTPS (https://)', value: 'https' },
  { label: 'SOCKS5 (socks5://)', value: 'socks5' },
];

const draft = ref<GlobalNetworkSettings>({
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

const proxyPasswordInput = ref('');
const probeTargetUrl = ref('https://api.openai.com/v1/models');
const sysProxy = computed(() => configStore.systemProxySnapshot);
const probeResult = computed(() => configStore.lastProxyTestResult);

function resetDraftFromStore() {
  const src = configStore.networkSettings;
  draft.value = JSON.parse(JSON.stringify(src));
  proxyPasswordInput.value = '';
}

watch(
  () => configStore.networkSettings,
  () => {
    resetDraftFromStore();
  },
  { deep: true, immediate: true }
);

onMounted(() => {
  configStore.refreshSystemProxySnapshot();
});

async function handleRefreshSystemProxy() {
  const snap = await configStore.refreshSystemProxySnapshot(probeTargetUrl.value);
  if (snap) {
    message.success(
      snap.isEnabled
        ? `检测到系统代理：${snap.effectiveHttpProxyUri}`
        : '当前系统未启用代理 (直连)'
    );
  }
}

function buildSettingsPayload(): GlobalNetworkSettings {
  const payload: GlobalNetworkSettings = JSON.parse(JSON.stringify(draft.value));
  if (proxyPasswordInput.value.trim()) {
    payload.customProxy.password = proxyPasswordInput.value.trim();
  }
  return payload;
}

async function handleSaveNetworkSettings() {
  const payload = buildSettingsPayload();
  const res = await configStore.saveNetworkSettings(payload);
  if (res.ok) {
    proxyPasswordInput.value = '';
    message.success(res.message || '全局网络与代理设置已保存并立即生效');
  } else {
    message.error(res.message || '保存网络配置失败');
  }
}

async function handleTestConnectivity() {
  const payload = buildSettingsPayload();
  const res = await configStore.testGlobalProxyConnectivity(probeTargetUrl.value, payload);
  if (res.ok && res.data?.success) {
    message.success(res.message || `连通测试通过 (${res.data.elapsedMs} ms)`);
  } else {
    message.warning(res.data?.errorMessage || res.message || '代理连通测试未通过，请查看下方诊断详情');
  }
}
</script>
