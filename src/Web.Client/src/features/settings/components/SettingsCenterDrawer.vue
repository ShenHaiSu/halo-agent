<template>
  <n-drawer v-model:show="visible" placement="right" :width="1020" style="max-width: 94vw" :mask-closable="true">
    <n-drawer-content title="设置与诊断中心" closable :native-scrollbar="false">
      <div class="settings-drawer-body min-w-0 max-w-full overflow-x-hidden">
        <n-tabs v-model:value="activeTab" type="card" placement="left" class="settings-left-tabs">
          <n-tab-pane name="appearance" tab="主题和样式">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <AppearanceSettingsPanel />
            </div>
          </n-tab-pane>

          <n-tab-pane name="providers" tab="模型与服务商">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <ProviderManagerView />
            </div>
          </n-tab-pane>

          <n-tab-pane name="network" tab="网络与代理">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <NetworkProxySettings />
            </div>
          </n-tab-pane>

          <n-tab-pane name="storage" tab="存储与日志">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <StorageSettingsPanel />
            </div>
          </n-tab-pane>

          <n-tab-pane name="runtime" tab="系统运行环境">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <SystemRuntimePanel />
            </div>
          </n-tab-pane>

          <n-tab-pane name="prompt" tab="提示词与预发送">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <PromptSettingsView />
            </div>
          </n-tab-pane>

          <n-tab-pane name="agent" tab="智能体行为">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <AgentBehaviorView />
            </div>
          </n-tab-pane>

          <n-tab-pane name="skill-memory" tab="技能与记忆">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <SkillMemoryView />
            </div>
          </n-tab-pane>

          <n-tab-pane name="usage" tab="用量分析">
            <div class="settings-tab-content min-w-0 max-w-full overflow-x-hidden">
              <UsageAnalyticsView v-if="activeTab === 'usage'" ref="usageViewRef" />
            </div>
          </n-tab-pane>
        </n-tabs>
      </div>
    </n-drawer-content>
  </n-drawer>
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick } from 'vue';
import ProviderManagerView from './ProviderManagerView.vue';
import NetworkProxySettings from './NetworkProxySettings.vue';
import StorageSettingsPanel from './StorageSettingsPanel.vue';
import SystemRuntimePanel from './SystemRuntimePanel.vue';
import AppearanceSettingsPanel from './AppearanceSettingsPanel.vue';
import PromptSettingsView from './PromptSettingsView.vue';
import AgentBehaviorView from './AgentBehaviorView.vue';
import SkillMemoryView from './SkillMemoryView.vue';
import { provideSettingsDrawer } from '../composables/useSettingsDrawer';
import { defineAsyncComponent } from 'vue';
const UsageAnalyticsView = defineAsyncComponent(() => import('./UsageAnalyticsView.vue'));
export type SettingsCenterTab =
  'appearance' | 'providers' | 'network' | 'storage' | 'runtime' | 'prompt' | 'agent' | 'skill-memory' | 'usage';

const props = defineProps<{
  show: boolean;
  defaultTab?: SettingsCenterTab;
}>();

const emit = defineEmits<{
  (e: 'update:show', value: boolean): void;
}>();

const activeTab = ref<SettingsCenterTab>(props.defaultTab || 'appearance');

const usageViewRef = ref<{ refresh: () => Promise<void>; resize: () => void } | null>(null);

watch(
  () => props.defaultTab,
  (val) => {
    if (val) activeTab.value = val;
  }
);

// 每次打开抽屉都按 defaultTab 落位（无参则首 Tab 外观），保证旧传参仍可定位
watch(
  () => props.show,
  (val) => {
    if (val) activeTab.value = props.defaultTab || 'appearance';
  }
);

// need12：用量 Tab 懒挂载后容器才有宽度，激活时下一帧 resize 图表
watch(activeTab, async (val) => {
  if (val === 'usage') {
    await nextTick();
    await nextTick();
    try {
      usageViewRef.value?.resize();
    } catch {
      /* ignore */
    }
  }
});

const visible = computed({
  get: () => props.show,
  set: (val: boolean) => emit('update:show', val),
});

// need23：向页内卡片提供"关闭抽屉"通道（如进入子会话后需让出主工作台）
provideSettingsDrawer({
  close: () => {
    visible.value = false;
  },
});
</script>
<style scoped>
.settings-drawer-body {
  margin: 0;
  padding: 0;
}
:deep(.n-drawer-body-content-wrapper) {
  padding: 10px 8px 8px 4px !important;
}
.settings-left-tabs {
  min-height: 60vh;
}
.settings-left-tabs :deep(.n-tabs-nav) {
  width: 116px;
  flex-shrink: 0;
  padding-left: 0;
  margin-left: 0;
}
.settings-left-tabs :deep(.n-tabs-nav-scroll-wrapper),
.settings-left-tabs :deep(.n-tabs-nav-scroll-content) {
  padding-left: 0;
  margin-left: 0;
}
.settings-left-tabs :deep(.n-tabs-tab) {
  justify-content: flex-start;
  padding: 4px 6px;
  font-size: 12px;
  margin-left: 0;
}
.settings-left-tabs :deep(.n-tab-pane) {
  padding: 0;
  flex: 1;
  min-width: 0;
}
.settings-tab-content {
  padding-left: 4px;
  padding-top: 0;
}
/* 信息密度：压缩各面板常用的外层间距 */
.settings-left-tabs :deep(.space-y-4 > :not([hidden]) ~ :not([hidden])) {
  --tw-space-y-reverse: 0;
  margin-top: calc(0.5rem * calc(1 - var(--tw-space-y-reverse)));
  margin-bottom: 0;
}
.settings-left-tabs :deep(.space-y-3 > :not([hidden]) ~ :not([hidden])) {
  --tw-space-y-reverse: 0;
  margin-top: calc(0.375rem * calc(1 - var(--tw-space-y-reverse)));
  margin-bottom: 0;
}
.settings-left-tabs :deep(.gap-4) {
  gap: 0.5rem;
}
.settings-left-tabs :deep(.gap-3) {
  gap: 0.375rem;
}
.settings-left-tabs :deep(.p-3) {
  padding: 0.375rem;
}
.settings-left-tabs :deep(.p-3\.5) {
  padding: 0.5rem;
}
/* n-card 紧凑化 */
.settings-left-tabs :deep(.n-card > .n-card-header) {
  padding: 6px 8px;
}
.settings-left-tabs :deep(.n-card > .n-card-header .n-card-header__title) {
  font-size: 13px;
}
.settings-left-tabs :deep(.n-card > .n-card__content) {
  padding: 6px 8px;
}
</style>
