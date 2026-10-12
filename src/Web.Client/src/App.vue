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
            <!-- 1. 顶部全局导航与工作台状态栏 (TopHeaderBar: 高度 40px, 固定不滚动) -->
            <header
              class="h-[40px] shrink-0 px-3 border-b border-subtle bg-sidebar flex items-center justify-between"
            >
              <!-- 左侧：侧栏折叠 + 项目切换 NPopselect + 当前会话名 -->
              <div class="flex items-center gap-2 min-w-0">
                <n-button
                  quaternary
                  size="small"
                  class="!px-1.5"
                  title="折叠/展开工作区侧栏"
                  @click="toggleSidebar"
                >
                  <template #icon>
                    <PanelLeft class="w-4 h-4" />
                  </template>
                </n-button>

                <div class="flex items-center gap-1.5 text-xs">
                  <n-popselect
                    v-model:value="activeWorkspaceId"
                    :options="workspaceOptions"
                    trigger="click"
                  >
                    <button
                      type="button"
                      class="flex items-center gap-1.5 px-2 py-1 rounded hover:bg-white/5 transition-colors text-primary font-medium"
                    >
                      <FolderGit2 class="w-3.5 h-3.5 text-accent" />
                      <span>{{ currentWorkspaceLabel }}</span>
                      <ChevronDown class="w-3.5 h-3.5 text-secondary" />
                    </button>
                  </n-popselect>

                  <span class="text-secondary">/</span>

                  <div class="flex items-center gap-1.5 px-1.5 py-0.5 rounded text-primary">
                    <MessageSquareCode class="w-3.5 h-3.5 text-secondary" />
                    <span class="truncate max-w-[260px] font-medium">{{ currentSessionTitle }}</span>
                  </div>
                </div>
              </div>

              <!-- 右侧：模型选择 NSelect + 主题切换 + 设置按钮 -->
              <div class="flex items-center gap-2.5">
                <div class="w-[260px]">
                  <n-select
                    :value="selectedProviderModelKey"
                    size="small"
                    :options="modelOptions"
                    placeholder="选择推理服务商与模型"
                    data-testid="top-model-select"
                    @update:value="handleSelectTopModel"
                  />
                </div>

                <n-button
                  quaternary
                  size="small"
                  class="!px-2"
                  title="切换暗黑/浅色主题"
                  @click="systemStore.toggleTheme()"
                >
                  <template #icon>
                    <Sun v-if="systemStore.themeMode === 'dark'" class="w-4 h-4" />
                    <Moon v-else class="w-4 h-4" />
                  </template>
                </n-button>

                <n-button
                  type="primary"
                  size="small"
                  data-testid="open-settings-btn"
                  @click="openSettingsModal('providers')"
                >
                  <template #icon>
                    <Settings class="w-3.5 h-3.5" />
                  </template>
                  设置中心
                </n-button>
              </div>
            </header>

            <!-- 2. 主体可拖拽分栏区 (高度 calc(100vh - 66px)) -->
            <main class="flex-1 min-h-0 overflow-hidden">
              <n-split
                direction="horizontal"
                :size="splitSize"
                :min="0.16"
                :max="0.35"
                @update:size="handleSplitChange"
              >
                <template #1>
                  <!-- Pane 1: 左侧工作区导航 -->
                  <aside class="h-full bg-sidebar border-r border-subtle flex flex-col p-2.5 gap-2.5 overflow-hidden">
                    <!-- 项目信息卡片 -->
                    <div class="p-2.5 rounded-md bg-surface border border-subtle space-y-1.5">
                      <div class="flex items-center justify-between">
                        <span class="text-xs font-semibold flex items-center gap-1.5">
                          <TerminalSquare class="w-3.5 h-3.5 text-accent" />
                          <span>当前工作路径</span>
                        </span>
                        <n-tag size="small" type="info" :bordered="false" class="font-mono text-[10px]">
                          CMD First
                        </n-tag>
                      </div>
                      <div class="text-[11px] text-secondary font-mono truncate" title="F:\MyProgram\CSharp\halo-agent">
                        F:\MyProgram\CSharp\halo-agent
                      </div>
                    </div>

                    <!-- 新建会话 NButton -->
                    <n-button
                      type="primary"
                      size="medium"
                      block
                      @click="handleNewPreviewSession"
                    >
                      <template #icon>
                        <Plus class="w-4 h-4" />
                      </template>
                      新建 Agent 会话
                    </n-button>

                    <!-- 会话分组列表 -->
                    <div class="flex-1 min-h-0 overflow-y-auto space-y-1 pr-0.5">
                      <div class="px-2 py-1 text-[11px] font-medium text-secondary uppercase tracking-wider">
                        今日会话 (Stage 02)
                      </div>

                      <div
                        v-for="session in sessions"
                        :key="session.id"
                        class="group flex items-center justify-between px-2.5 py-2 rounded-md cursor-pointer text-xs transition-colors border"
                        :class="
                          session.id === activeSessionId
                            ? 'bg-elevated border-accent/40 text-primary font-medium'
                            : 'border-transparent text-secondary hover:bg-surface hover:text-primary'
                        "
                        @click="activeSessionId = session.id"
                      >
                        <div class="flex items-center gap-2 min-w-0">
                          <Cpu class="w-3.5 h-3.5 shrink-0 text-accent" />
                          <span class="truncate">{{ session.title }}</span>
                        </div>
                        <span class="font-mono text-[10px] text-secondary shrink-0">
                          {{ session.hash }}
                        </span>
                      </div>
                    </div>
                  </aside>
                </template>

                <template #2>
                  <!-- Pane 2: 右侧主工作台 (Flex-1, 高度 calc(100vh - 66px)) -->
                  <section class="h-full flex flex-col bg-app overflow-hidden">
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
                                Stage 02 Ready
                              </n-tag>
                            </div>
                            <p class="text-xs text-secondary leading-relaxed">
                              当前宿主采用 <code class="font-mono text-primary">.NET 6.0</code> +
                              <code class="font-mono text-primary">CefSharp.WinForms 109.1.110</code> +
                              <code class="font-mono text-primary">DPAPI 密钥保护</code> +
                              <code class="font-mono text-primary">ProxyHub 动态代理中枢</code>。
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
  PanelLeft,
  FolderGit2,
  ChevronDown,
  MessageSquareCode,
  Sun,
  Moon,
  Settings,
  TerminalSquare,
  Plus,
  Cpu,
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
import StorageSettingsPanel from '@/components/settings/StorageSettingsPanel.vue';
import NetworkProxySettings from '@/components/settings/NetworkProxySettings.vue';
import ProviderManagerView from '@/components/settings/ProviderManagerView.vue';
import SettingsCenterModal from '@/components/settings/SettingsCenterModal.vue';

const systemStore = useSystemStore();
const configStore = useConfigStore();

const showSettingsModal = ref(false);
const settingsModalTab = ref<'providers' | 'network' | 'storage' | 'runtime'>('providers');
const inlineWorkspaceTab = ref<'providers' | 'network' | 'storage'>('providers');

const splitSize = ref(0.21);
const lastExpandedSplitSize = ref(0.21);
const composerDraft = ref('');

const activeWorkspaceId = ref('ws-halo-agent');
const workspaceOptions = [
  { label: 'halo-agent (F:\\MyProgram\\CSharp\\halo-agent)', value: 'ws-halo-agent' },
];

const modelOptions = computed(() => {
  const opts: Array<{ label: string; value: string }> = [];
  for (const p of configStore.providersDoc.providers) {
    if (!p.enabled) continue;
    for (const m of p.models) {
      if (!m.enabled) continue;
      opts.push({
        label: `${p.name} · ${m.displayName || m.id} [${m.protocol}]`,
        value: `${p.id}::${m.id}`,
      });
    }
  }
  return opts;
});

const selectedProviderModelKey = computed(() => {
  const p = configStore.activeProvider;
  const m = configStore.activeModel;
  if (!p || !m) return '';
  return `${p.id}::${m.id}`;
});

async function handleSelectTopModel(val: string) {
  const [providerId, modelId] = val.split('::');
  if (providerId && modelId) {
    await configStore.setActiveModel(providerId, modelId);
  }
}

function openSettingsModal(tab: 'providers' | 'network' | 'storage' | 'runtime') {
  settingsModalTab.value = tab;
  showSettingsModal.value = true;
}

const sessions = ref([
  { id: 'sess-01', title: 'Stage 02 配置与代理中枢自检', hash: 'a8f19c3d4e20b71a' },
  { id: 'sess-02', title: 'Win7 CefSharp 109 兼容性验证', hash: '7c4e1290b3d581ef' },
]);
const activeSessionId = ref('sess-01');

const activeTheme = computed(() => (systemStore.themeMode === 'dark' ? darkTheme : null));
const activeThemeOverrides = computed(() =>
  systemStore.themeMode === 'dark' ? haloDarkThemeOverrides : haloLightThemeOverrides
);

const currentWorkspaceLabel = computed(() => 'halo-agent');
const currentSessionTitle = computed(
  () => sessions.value.find((s) => s.id === activeSessionId.value)?.title || '默认会话'
);
const activeSessionHash = computed(
  () => sessions.value.find((s) => s.id === activeSessionId.value)?.hash || 'a8f19c3d4e20b71a'
);

function handleSplitChange(val: number) {
  splitSize.value = val;
  if (val >= 0.16) {
    lastExpandedSplitSize.value = val;
  }
}

function toggleSidebar() {
  if (splitSize.value > 0.05) {
    lastExpandedSplitSize.value = splitSize.value;
    splitSize.value = 0;
  } else {
    splitSize.value = lastExpandedSplitSize.value || 0.21;
  }
}

function handleNewPreviewSession() {
  const idx = sessions.value.length + 1;
  const id = `sess-0${idx}`;
  const hash = Math.random().toString(16).slice(2, 10) + Math.random().toString(16).slice(2, 10);
  sessions.value.unshift({
    id,
    title: `新建会话 #${idx}`,
    hash,
  });
  activeSessionId.value = id;
}

async function handleSendComposerPreview() {
  if (!composerDraft.value.trim()) return;
  await systemStore.writeTestLogEntry('app', `User prompt preview submitted: ${composerDraft.value}`);
  composerDraft.value = '';
}

async function handleRefreshAll() {
  await Promise.all([systemStore.refreshAll(), configStore.initialize()]);
}

onMounted(async () => {
  systemStore.applyThemeClass('dark');
  systemStore.setupEventListeners();
  await Promise.all([systemStore.refreshAll(), configStore.initialize()]);
});
</script>
