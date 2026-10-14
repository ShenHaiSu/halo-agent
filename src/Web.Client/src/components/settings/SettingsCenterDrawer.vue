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

const props = defineProps<{
  show: boolean;
  defaultTab?: 'providers' | 'network' | 'storage' | 'runtime';
}>();

const emit = defineEmits<{
  (e: 'update:show', value: boolean): void;
}>();

const activeTab = ref<'providers' | 'network' | 'storage' | 'runtime'>(
  props.defaultTab || 'providers'
);

watch(
  () => props.defaultTab,
  (val) => {
    if (val) activeTab.value = val;
  }
);

const visible = computed({
  get: () => props.show,
  set: (val: boolean) => emit('update:show', val),
});
</script>
