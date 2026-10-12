<template>
  <n-modal
    v-model:show="visible"
    preset="card"
    title="Halo-Agent 设置与诊断中心"
    style="width: 960px; max-width: 95vw"
    :bordered="true"
    size="small"
  >
    <template #header-extra>
      <n-tag size="small" type="info" :bordered="false" class="font-mono">
        Stage 02 Config &amp; ProxyHub
      </n-tag>
    </template>

    <n-tabs
      v-model:value="activeTab"
      type="line"
      placement="left"
      style="min-height: 520px"
    >
      <n-tab-pane name="providers" tab="模型与服务商">
        <div class="pl-2">
          <ProviderManagerView />
        </div>
      </n-tab-pane>

      <n-tab-pane name="network" tab="网络与代理">
        <div class="pl-2">
          <NetworkProxySettings />
        </div>
      </n-tab-pane>

      <n-tab-pane name="storage" tab="存储与日志">
        <div class="pl-2">
          <StorageSettingsPanel />
        </div>
      </n-tab-pane>

      <n-tab-pane name="runtime" tab="系统运行环境">
        <div class="pl-2">
          <SystemRuntimePanel />
        </div>
      </n-tab-pane>
    </n-tabs>
  </n-modal>
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
