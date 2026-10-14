<template>
  <n-drawer
    v-model:show="visible"
    placement="right"
    :width="1020"
    style="max-width: 94vw;"
    :mask-closable="true"
  >
    <n-drawer-content
      title="设置与诊断中心"
      closable
      :native-scrollbar="false"
    >
      <div class="settings-drawer-body min-w-0 max-w-full overflow-x-hidden">
        <n-tabs
          v-model:value="activeTab"
          type="line"
          placement="top"
          class="sticky top-0 z-10 bg-[var(--bg-surface)]"
        >
          <n-tab-pane name="appearance" tab="主题和样式">
            <div class="pt-3 min-w-0 max-w-full overflow-x-hidden">
              <AppearanceSettingsPanel />
            </div>
          </n-tab-pane>

          <n-tab-pane name="providers" tab="模型与服务商">
            <div class="pt-3 min-w-0 max-w-full overflow-x-hidden">
              <ProviderManagerView />
            </div>
          </n-tab-pane>

          <n-tab-pane name="network" tab="网络与代理">
            <div class="pt-3 min-w-0 max-w-full overflow-x-hidden">
              <NetworkProxySettings />
            </div>
          </n-tab-pane>

          <n-tab-pane name="storage" tab="存储与日志">
            <div class="pt-3 min-w-0 max-w-full overflow-x-hidden">
              <StorageSettingsPanel />
            </div>
          </n-tab-pane>

          <n-tab-pane name="runtime" tab="系统运行环境">
            <div class="pt-3 min-w-0 max-w-full overflow-x-hidden">
              <SystemRuntimePanel />
            </div>
          </n-tab-pane>
        </n-tabs>
      </div>
    </n-drawer-content>
  </n-drawer>
</template>

<script setup lang="ts">
import { ref, computed, watch } from 'vue';
import ProviderManagerView from './ProviderManagerView.vue';
import NetworkProxySettings from './NetworkProxySettings.vue';
import StorageSettingsPanel from './StorageSettingsPanel.vue';
import SystemRuntimePanel from './SystemRuntimePanel.vue';
import AppearanceSettingsPanel from './AppearanceSettingsPanel.vue';

export type SettingsCenterTab = 'appearance' | 'providers' | 'network' | 'storage' | 'runtime';

const props = defineProps<{
  show: boolean;
  defaultTab?: SettingsCenterTab;
}>();

const emit = defineEmits<{
  (e: 'update:show', value: boolean): void;
}>();

const activeTab = ref<SettingsCenterTab>(
  props.defaultTab || 'appearance'
);

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

const visible = computed({
  get: () => props.show,
  set: (val: boolean) => emit('update:show', val),
});
</script>
