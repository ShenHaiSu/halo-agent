<template>
  <n-config-provider
    :theme="activeTheme"
    :theme-overrides="activeThemeOverrides"
    :locale="zhCN"
    :date-locale="dateZhCN"
  >
    <n-dialog-provider>
      <n-notification-provider placement="bottom-right">
        <n-message-provider>
          <div class="w-screen h-screen overflow-hidden flex flex-col bg-app text-primary select-none">
            <!-- 1. 主体可拖拽分栏区 -->
            <main class="flex-1 min-h-0 overflow-hidden">
              <n-split
                direction="horizontal"
                :size="splitSize"
                :min="0.16"
                :max="0.35"
                @update:size="handleSplitChange"
              >
                <template #1>
                  <!-- Pane 1: 左侧工作区与多会话导航 (Workspace & Session Sidebar) -->
                  <WorkspaceSessionSidebar />
                </template>

                <template #2>
                  <!-- Pane 2: 右侧主工作台 -->
                  <section class="h-full flex flex-col bg-app overflow-hidden">
                    <!-- 顶部工作台导航与上下文水位仪 -->
                    <WorkbenchTopBar @open-settings="openSettingsModal('providers')" />

                    <!-- 消息时间轴与配置/代理/存储快速工作台 -->
                    <div class="flex-1 min-h-0 overflow-y-auto p-5 space-y-4">
                      <!-- 欢迎与宿主底座状态横幅 -->
                      <div class="max-w-4xl mx-auto space-y-4">
                        <div class="p-4 rounded-lg bg-surface border border-subtle flex items-start justify-between gap-4">
                          <div class="space-y-1.5">
                            <div class="flex items-center gap-2">
                              <Shield class="w-5 h-5 text-accent" />
                              <h1 class="text-sm font-semibold">
                                Halo-Agent 跨代桌面端底座 (Win7 SP1 / Win10 / Win11)
                              </h1>
                              <n-tag type="success" size="small" :bordered="false">
                                Stage 04 Ready
                              </n-tag>
                            </div>
                            <p class="text-xs text-secondary leading-relaxed">
                              当前宿主采用 <code class="font-mono text-primary">.NET 6.0</code> +
                              <code class="font-mono text-primary">CefSharp.WinForms 109.1.110</code> +
                              <code class="font-mono text-primary">多项目隔离 & 多会话持久化</code> +
                              <code class="font-mono text-primary">上下文预算 & 成对裁剪</code>。
                            </p>
                          </div>

                          <n-space :size="8" class="shrink-0">
                            <n-button
                              size="small"
                              secondary
                              @click="handleRefreshAll"
                            >
                              <template #icon>
                                <RefreshCw class="w-3.5 h-3.5" />
                              </template>
                              刷新状态
                            </n-button>
                            <n-button
                              size="small"
                              type="info"
                              secondary
                              data-testid="quick-open-network-btn"
                              @click="openSettingsModal('network')"
                            >
                              <template #icon>
                                <Network class="w-3.5 h-3.5" />
                              </template>
                              网络与代理设置
                            </n-button>
                            <n-button
                              size="small"
                              type="primary"
                              data-testid="quick-open-providers-btn"
                              @click="openSettingsModal('providers')"
                            >
                              <template #icon>
                                <Server class="w-3.5 h-3.5" />
                              </template>
                              管理服务商与模型
                            </n-button>
                          </n-space>
                        </div>

                        <!-- 核心遥测四栏卡片 -->
                        <div class="grid grid-cols-4 gap-3">
                          <!-- 1. 当前激活服务商与模型 -->
                          <div class="p-3.5 rounded-lg bg-surface border border-subtle space-y-2">
                            <div class="flex items-center justify-between text-xs font-medium">
                              <span class="flex items-center gap-1.5">
                                <Server class="w-4 h-4 text-blue-400" />
                                <span>当前模型与协议</span>
                              </span>
                              <n-tag
                                :type="configStore.activeModel?.protocol === 'resp' ? 'info' : 'success'"
                                size="small"
                                :bordered="false"
                                class="font-mono"
                              >
                                {{ configStore.activeModel?.protocol || 'chat' }}
                              </n-tag>
                            </div>
                            <div class="text-xs font-mono text-primary truncate">
                              {{ configStore.activeProvider?.name || 'OpenAI' }} /
                              {{ configStore.activeModel?.id || 'gpt-4o' }}
                            </div>
                            <div class="text-[11px] text-secondary flex items-center justify-between">
                              <span>
                                Context:
                                {{
                                  ((configStore.activeModel?.maxContextTokens || 128000) / 1000).toFixed(0)
                                }}K
                              </span>
                              <button
                                type="button"
                                class="text-accent hover:underline flex items-center gap-1"
                                @click="openSettingsModal('providers')"
                              >
                                <span>配置模型</span>
                                <ArrowUpRight class="w-3 h-3" />
                              </button>
                            </div>
                          </div>

                          <!-- 2. 全局网络代理中枢 -->
                          <div class="p-3.5 rounded-lg bg-surface border border-subtle space-y-2">
                            <div class="flex items-center justify-between text-xs font-medium">
                              <span class="flex items-center gap-1.5">
                                <Network class="w-4 h-4 text-sky-400" />
                                <span>全局网络代理中枢</span>
                              </span>
                              <n-tag
                                :type="
                                  configStore.networkSettings.mode === 'None'
                                    ? 'warning'
                                    : configStore.networkSettings.mode === 'Custom'
                                    ? 'info'
                                    : 'success'
                                "
                                size="small"
                                :bordered="false"
                                class="font-mono"
                              >
                                {{ configStore.networkSettings.mode.toUpperCase() }}
                              </n-tag>
                            </div>
                            <div
                              class="text-xs font-mono text-primary truncate"
                              :title="configStore.globalProxySummaryText"
                            >
                              {{ configStore.globalProxySummaryText }}
                            </div>
                            <div class="text-[11px] text-secondary flex items-center justify-between">
                              <span>超时: {{ configStore.networkSettings.defaultConnectTimeoutSeconds }}s</span>
                              <button
                                type="button"
                                class="text-accent hover:underline flex items-center gap-1"
                                @click="openSettingsModal('network')"
                              >
                                <span>切换代理</span>
                                <ArrowUpRight class="w-3 h-3" />
                              </button>
                            </div>
                          </div>

                          <!-- 3. C盘可配置存储中枢 -->
                          <div class="p-3.5 rounded-lg bg-surface border border-subtle space-y-2">
                            <div class="flex items-center justify-between text-xs font-medium">
                              <span class="flex items-center gap-1.5">
                                <HardDrive class="w-4 h-4 text-accent" />
                                <span>两级引导存储中枢</span>
                              </span>
                              <n-tag
                                :type="systemStore.storageConfig?.isWritable !== false ? 'success' : 'error'"
                                size="small"
                                :bordered="false"
                              >
                                {{ systemStore.storageConfig?.isWritable !== false ? '可读写' : '只读' }}
                              </n-tag>
                            </div>
                            <div
                              class="text-xs font-mono text-primary truncate"
                              :title="systemStore.storageConfig?.dataRootPath"
                            >
                              {{ systemStore.storageConfig?.dataRootPath || 'C:\\HaloAgentData' }}
                            </div>
                            <div class="text-[11px] text-secondary flex items-center justify-between">
                              <span>可用：{{ systemStore.diskFreeGbText }} GB</span>
                              <button
                                type="button"
                                class="text-accent hover:underline flex items-center gap-1"
                                @click="openSettingsModal('storage')"
                              >
                                <span>修改路径</span>
                                <ArrowUpRight class="w-3 h-3" />
                              </button>
                            </div>
                          </div>

                          <!-- 4. 三通道分级日志监控 -->
                          <div class="p-3.5 rounded-lg bg-surface border border-subtle space-y-2">
                            <div class="flex items-center justify-between text-xs font-medium">
                              <span class="flex items-center gap-1.5">
                                <FileText class="w-4 h-4 text-emerald-400" />
                                <span>三通道分级日志</span>
                              </span>
                              <n-tag type="info" size="small" :bordered="false">
                                DPAPI 脱敏
                              </n-tag>
                            </div>
                            <div
                              class="text-xs font-mono text-primary truncate"
                              :title="systemStore.systemInfo?.logsDirectory"
                            >
                              {{ systemStore.systemInfo?.logsDirectory || 'C:\\HaloAgentData\\logs' }}
                            </div>
                            <div class="text-[11px] text-secondary flex items-center justify-between">
                              <span>保留：{{ systemStore.storageConfig?.logRetentionDays ?? 14 }} 天</span>
                              <button
                                type="button"
                                class="text-accent hover:underline flex items-center gap-1"
                                @click="systemStore.openInExplorer(systemStore.systemInfo?.logsDirectory)"
                              >
                                <span>打开目录</span>
                                <ArrowUpRight class="w-3 h-3" />
                              </button>
                            </div>
                          </div>
                        </div>

                        <!-- 主工作区快速切换配置视图 (服务商 & 模型 / 网络 & 代理 / 存储 & 日志) -->
                        <n-card size="small" :bordered="true">
                          <n-tabs v-model:value="inlineWorkspaceTab" type="line" size="small">
                            <n-tab-pane name="providers" tab="服务商与模型配置 (Providers & Models)">
                              <div class="pt-2">
                                <ProviderManagerView />
                              </div>
                            </n-tab-pane>
                            <n-tab-pane name="network" tab="全局网络与代理中枢 (ProxyHub)">
                              <div class="pt-2">
                                <NetworkProxySettings />
                              </div>
                            </n-tab-pane>
                            <n-tab-pane name="storage" tab="数据存储与三通道日志 (Storage & Logs)">
                              <div class="pt-2">
                                <StorageSettingsPanel />
                              </div>
                            </n-tab-pane>
                          </n-tabs>
                        </n-card>
                      </div>
                    </div>

                    <!-- 底部悬浮式多功能 Prompt Composer 输入舱 -->
                    <div class="shrink-0 px-5 pb-3 pt-2 bg-app border-t border-subtle">
                      <div class="max-w-4xl mx-auto rounded-lg bg-surface border border-subtle p-2.5 space-y-2">
                        <n-input
                          v-model:value="composerDraft"
                          type="textarea"
                          :autosize="{ minRows: 2, maxRows: 4 }"
                          placeholder="输入任务指令（Stage 05/06 将默认优先通过 cmd.exe / python 执行工具调用）..."
                          class="!bg-transparent text-xs"
                        />
                        <div class="flex items-center justify-between">
                          <div class="flex items-center gap-2 text-[11px] text-secondary">
                            <span class="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-elevated font-mono">
                              <Terminal class="w-3 h-3 text-accent" />
                              Preferred Shell: cmd.exe -> python
                            </span>
                            <span class="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-elevated font-mono">
                              <Layers class="w-3 h-3 text-emerald-400" />
                              Active: {{ configStore.activeModel?.id || 'gpt-4o' }} ({{
                                configStore.activeModel?.protocol || 'chat'
                              }})
                            </span>
                          </div>

                          <n-button
                            type="primary"
                            size="small"
                            :disabled="!composerDraft.trim()"
                            @click="handleSendComposerPreview"
                          >
                            <template #icon>
                              <Send class="w-3.5 h-3.5" />
                            </template>
                            发送指令
                          </n-button>
                        </div>
                      </div>
                    </div>
                  </section>
                </template>
              </n-split>
            </main>

            <!-- 3. 底部遥测与系统状态栏 (StatusBar: 高度 26px, 仿 VS Code 底部状态条) -->
            <footer
              class="h-[26px] shrink-0 px-3 bg-sidebar border-t border-subtle flex items-center justify-between text-[11px] text-secondary font-mono"
            >
              <div class="flex items-center gap-4 min-w-0">
                <span class="flex items-center gap-1.5 text-primary">
                  <Monitor class="w-3.5 h-3.5 text-accent" />
                  <span>OS: {{ systemStore.systemInfo?.osDescription || 'Windows Desktop' }}</span>
                </span>

                <span
                  class="flex items-center gap-1.5 truncate cursor-pointer hover:text-primary"
                  title="点击配置数据与日志存储目录"
                  @click="openSettingsModal('storage')"
                >
                  <HardDrive class="w-3.5 h-3.5 text-emerald-400" />
                  <span>存储: {{ systemStore.storageConfig?.dataRootPath || 'C:\\HaloAgentData' }}</span>
                </span>
              </div>

              <div class="flex items-center gap-4 shrink-0">
                <span
                  class="flex items-center gap-1 cursor-pointer hover:text-primary"
                  title="点击配置全局或服务商代理"
                  data-testid="statusbar-proxy-indicator"
                  @click="openSettingsModal('network')"
                >
                  <Network class="w-3.5 h-3.5 text-sky-400" />
                  <span>代理: {{ configStore.globalProxySummaryText }}</span>
                </span>
                <span class="flex items-center gap-1">
                  <Hash class="w-3.5 h-3.5 text-amber-400" />
                  <span>SessionHash: {{ activeSessionHash }}</span>
                </span>
              </div>
            </footer>

            <!-- 设置中心模态框 -->
            <SettingsCenterModal
              v-model:show="showSettingsModal"
              :default-tab="settingsModalTab"
            />
          </div>
        </n-message-provider>
      </n-notification-provider>
    </n-dialog-provider>
  </n-config-provider>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { darkTheme, zhCN, dateZhCN } from 'naive-ui';
import {
  Shield,
  RefreshCw,
  Server,
  HardDrive,
  ArrowUpRight,
  FileText,
  Terminal,
  Layers,
  Send,
  Monitor,
  Network,
  Hash,
} from 'lucide-vue-next';
import { haloDarkThemeOverrides, haloLightThemeOverrides } from '@/theme/naiveTheme';
import { useSystemStore } from '@/stores/systemStore';
import { useConfigStore } from '@/stores/configStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import WorkspaceSessionSidebar from '@/components/sidebar/WorkspaceSessionSidebar.vue';
import WorkbenchTopBar from '@/components/workbench/WorkbenchTopBar.vue';
import StorageSettingsPanel from '@/components/settings/StorageSettingsPanel.vue';
import NetworkProxySettings from '@/components/settings/NetworkProxySettings.vue';
import ProviderManagerView from '@/components/settings/ProviderManagerView.vue';
import SettingsCenterModal from '@/components/settings/SettingsCenterModal.vue';

const systemStore = useSystemStore();
const configStore = useConfigStore();
const workspaceStore = useWorkspaceStore();

const showSettingsModal = ref(false);
const settingsModalTab = ref<'providers' | 'network' | 'storage' | 'runtime'>('providers');
const inlineWorkspaceTab = ref<'providers' | 'network' | 'storage'>('providers');

const splitSize = ref(0.22);
const lastExpandedSplitSize = ref(0.22);
const composerDraft = ref('');

function openSettingsModal(tab: 'providers' | 'network' | 'storage' | 'runtime') {
  settingsModalTab.value = tab;
  showSettingsModal.value = true;
}

const activeTheme = computed(() => (systemStore.themeMode === 'dark' ? darkTheme : null));
const activeThemeOverrides = computed(() =>
  systemStore.themeMode === 'dark' ? haloDarkThemeOverrides : haloLightThemeOverrides
);

const activeSessionHash = computed(
  () => workspaceStore.activeSession?.sessionHash || '-'
);

function handleSplitChange(val: number) {
  splitSize.value = val;
  if (val >= 0.16) {
    lastExpandedSplitSize.value = val;
  }
}

async function handleSendComposerPreview() {
  if (!composerDraft.value.trim()) return;
  await systemStore.writeTestLogEntry('app', `User prompt preview submitted: ${composerDraft.value}`);
  composerDraft.value = '';
}

async function handleRefreshAll() {
  await Promise.all([systemStore.refreshAll(), configStore.initialize(), workspaceStore.initialize()]);
}

onMounted(async () => {
  systemStore.applyThemeClass('dark');
  systemStore.setupEventListeners();
  await Promise.all([systemStore.refreshAll(), configStore.initialize(), workspaceStore.initialize()]);
});
</script>
