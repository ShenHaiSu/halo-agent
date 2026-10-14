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

                    <!-- Stage 06 Agent 工作台：消息时间轴 + 输入舱 -->
                    <WorkbenchView />
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

            <!-- 设置中心抽屉 -->
            <SettingsCenterDrawer
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
  Monitor,
  HardDrive,
  Network,
  Hash,
} from 'lucide-vue-next';
import { buildHaloThemeOverrides } from '@/theme/appearance';
import { useAppearanceStore } from '@/stores/appearanceStore';
import { useSystemStore } from '@/stores/systemStore';
import { useConfigStore } from '@/stores/configStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import WorkspaceSessionSidebar from '@/components/sidebar/WorkspaceSessionSidebar.vue';
import WorkbenchTopBar from '@/components/workbench/WorkbenchTopBar.vue';
import WorkbenchView from '@/views/WorkbenchView.vue';
import SettingsCenterDrawer from '@/components/settings/SettingsCenterDrawer.vue';

const systemStore = useSystemStore();
const appearanceStore = useAppearanceStore();
const configStore = useConfigStore();
const workspaceStore = useWorkspaceStore();

const showSettingsModal = ref(false);
const settingsModalTab = ref<'appearance' | 'providers' | 'network' | 'storage' | 'runtime'>('appearance');

const splitSize = ref(0.22);
const lastExpandedSplitSize = ref(0.22);

function openSettingsModal(tab: 'appearance' | 'providers' | 'network' | 'storage' | 'runtime') {
  settingsModalTab.value = tab;
  showSettingsModal.value = true;
}

const activeTheme = computed(() => (systemStore.themeMode === 'dark' ? darkTheme : null));
const activeThemeOverrides = computed(() =>
  buildHaloThemeOverrides(appearanceStore.settings, appearanceStore.themeMode)
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

onMounted(async () => {
  appearanceStore.loadAndApply();
  systemStore.setupEventListeners();
  await Promise.all([systemStore.refreshAll(), configStore.initialize(), workspaceStore.initialize()]);
});
</script>
