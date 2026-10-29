<!-- src/Web.Client/src/components/settings/SettingsApp.vue -->
<!-- 设置页容器：7 分页 + 窄窗全屏 + DirtyGuard 拦截切页/关页。 -->
<script lang="ts">
import { ref as sharedRef } from 'vue';
export type SettingsTab =
  | 'general'
  | 'model'
  | 'network'
  | 'permissions'
  | 'mcp'
  | 'assets'
  | 'about';
const settingsTab = sharedRef<SettingsTab>('general');

/** 跨组件打开设置（指定分页）。运行时调用，pinia 已就绪。 */
export function openSettings(tab: SettingsTab = 'general'): void {
  settingsTab.value = tab;
  // 延迟 import 避免模块循环？直接用 ui store（运行时 pinia 已安装）。
  import('@/stores/ui')
    .then((m) => {
      m.useUiStore().openSettings();
    })
    .catch(() => {
      /* 忽略 */
    });
}

export function useSettingsTab() {
  return settingsTab;
}
</script>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { useUiStore } from '@/stores/ui';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import SettingsNav, { type SettingsNavItem } from './SettingsNav.vue';
import HButton from '@/components/base/HButton.vue';
import HDialog from '@/components/base/HDialog.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import GeneralPage from './pages/GeneralPage.vue';
import ModelPage from './pages/ModelPage.vue';
import NetworkPage from './pages/NetworkPage.vue';
import PermissionsPage from './pages/PermissionsPage.vue';
import McpPage from './pages/McpPage.vue';
import AssetsPage from './pages/AssetsPage.vue';
import AboutPage from './pages/AboutPage.vue';

interface SettingsPage {
  isDirty: () => boolean;
  resetToSaved: () => void;
}

const ui = useUiStore();
const tab = settingsTab;

const generalPage = ref<SettingsPage | null>(null);
const modelPage = ref<SettingsPage | null>(null);
const networkPage = ref<SettingsPage | null>(null);
const permissionsPage = ref<SettingsPage | null>(null);
const mcpPage = ref<SettingsPage | null>(null);
const assetsPage = ref<SettingsPage | null>(null);
const aboutPage = ref<SettingsPage | null>(null);

const activePage = computed<SettingsPage | null>(() => {
  switch (tab.value) {
    case 'general':
      return generalPage.value;
    case 'model':
      return modelPage.value;
    case 'network':
      return networkPage.value;
    case 'permissions':
      return permissionsPage.value;
    case 'mcp':
      return mcpPage.value;
    case 'assets':
      return assetsPage.value;
    case 'about':
      return aboutPage.value;
    default:
      return null;
  }
});

const currentDirty = computed(() => activePage.value?.isDirty() === true);
const guard = useDirtyGuard(currentDirty, () => activePage.value?.resetToSaved());

const NAV: SettingsNavItem[] = [
  { key: 'general', label: '常规', icon: 'settings' },
  { key: 'model', label: '模型', icon: 'zap' },
  { key: 'network', label: '网络', icon: 'link' },
  { key: 'permissions', label: '权限', icon: 'shield-check' },
  { key: 'mcp', label: '扩展', icon: 'layers' },
  { key: 'assets', label: '资源', icon: 'folder' },
  { key: 'about', label: '关于', icon: 'info' },
];

function selectTab(t: SettingsTab): void {
  guard.confirmLeave(() => {
    tab.value = t;
  });
}

function requestClose(): void {
  guard.confirmLeave(() => ui.closeModal());
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="ui.activeModal === 'settings'"
      class="settings-overlay"
      role="dialog"
      aria-modal="true"
      aria-label="设置"
    >
      <div class="settings-panel">
        <header class="settings-head">
          <h2 class="settings-title">设置</h2>
          <span v-if="currentDirty" class="dirty-hint" role="status">有未保存的改动</span>
          <span class="spacer" />
          <button type="button" class="close-btn" aria-label="关闭设置" @click="requestClose">
            <HaloIcon name="x" :size="16" />
          </button>
        </header>

        <div class="settings-body">
          <SettingsNav :items="NAV" :active="tab" @select="selectTab" />
          <div class="settings-page">
            <GeneralPage v-if="tab === 'general'" ref="generalPage" />
            <ModelPage v-else-if="tab === 'model'" ref="modelPage" />
            <NetworkPage v-else-if="tab === 'network'" ref="networkPage" />
            <PermissionsPage v-else-if="tab === 'permissions'" ref="permissionsPage" />
            <McpPage v-else-if="tab === 'mcp'" ref="mcpPage" />
            <AssetsPage v-else-if="tab === 'assets'" ref="assetsPage" />
            <AboutPage v-else ref="aboutPage" />
          </div>
        </div>

        <HDialog
          :open="guard.dialogOpen.value"
          title="放弃未保存的改动？"
          width="sm"
          @update:open="guard.cancelLeave()"
        >
          <p class="dlg-p">当前分页有未保存的改动，离开后将丢失。</p>
          <template #footer>
            <HButton variant="secondary" size="sm" @click="guard.cancelLeave">继续编辑</HButton>
            <HButton variant="danger" size="sm" @click="guard.confirmDiscard">放弃改动</HButton>
          </template>
        </HDialog>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.settings-overlay {
  position: fixed;
  inset: 0;
  z-index: var(--z-modal);
  background: hsl(var(--bg-overlay));
  display: flex;
  justify-content: flex-end;
}
.settings-panel {
  width: 75%;
  width: min(1400px, 100%, max(75%, 900px));
  height: 100%;
  background: hsl(var(--bg));
  border-left: 1px solid hsl(var(--border));
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.settings-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-3) var(--space-4);
  border-bottom: 1px solid hsl(var(--border));
  flex: none;
}
.settings-title {
  font-size: var(--text-base);
  font-weight: 600;
  margin: 0;
}
.dirty-hint {
  font-size: var(--text-xs);
  color: hsl(var(--warning));
}
.spacer {
  flex: 1;
  min-width: 0;
}
.close-btn {
  background: transparent;
  border: 0;
  border-radius: var(--radius-xs);
  padding: 4px;
  cursor: pointer;
  color: hsl(var(--fg-subtle));
}
.close-btn:hover {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg));
}
.settings-body {
  flex: 1;
  min-height: 0;
  display: flex;
  gap: var(--space-3);
  padding: var(--space-3);
  overflow: hidden;
}
.settings-body > nav {
  flex: none;
  width: 148px;
}
.settings-page {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
}
.dlg-p {
  margin: 0;
  font-size: var(--text-sm);
  line-height: 1.6;
}
@media (max-width: 900px) {
  .settings-overlay {
    justify-content: stretch;
  }
  .settings-panel {
    width: 100%;
    border-left: 0;
  }
  .settings-body {
    flex-direction: column;
  }
  .settings-body > nav {
    width: 100%;
  }
}
</style>
