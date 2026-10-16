import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  getHostBridge,
  haloEventBus,
  type AgentConfigDocument,
  type BridgeResponse,
} from '@/bridge/hostBridge';

/** need08：智能体行为配置 store（对标 promptStore/configStore）。 */
export const useAgentConfigStore = defineStore('agentConfig', () => {
  const config = ref<AgentConfigDocument | null>(null);
  const loading = ref(false);
  const saving = ref(false);
  const lastSavedHash = ref('');
  const lastError = ref<{ field: string; message: string } | null>(null);

  const dirty = computed(() => {
    if (!config.value) return false;
    return JSON.stringify(config.value) !== lastSavedHash.value;
  });

  function markSaved() {
    lastSavedHash.value = config.value ? JSON.stringify(config.value) : '';
    lastError.value = null;
  }

  function touchDirty() {
    lastError.value = null;
  }

  async function loadConfig(): Promise<void> {
    loading.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getAgentConfig();
      if (res.ok && res.data) {
        config.value = res.data;
        markSaved();
      }
    } finally {
      loading.value = false;
    }
  }

  async function saveConfig(): Promise<BridgeResponse<AgentConfigDocument>> {
    if (!config.value) throw new Error('no-config');
    saving.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.saveAgentConfig(config.value);
      if (res.ok && res.data) {
        config.value = res.data;
        markSaved();
      } else if (res.code === 'AGENT_VALIDATION') {
        lastError.value = { field: guessField(res.message || ''), message: res.message || '校验失败' };
      }
      return res;
    } finally {
      saving.value = false;
    }
  }

  async function resetField(field: string): Promise<BridgeResponse<AgentConfigDocument>> {
    const bridge = getHostBridge();
    const res = await bridge.resetAgentConfig(field);
    if (res.ok && res.data) {
      config.value = res.data;
      markSaved();
    }
    return res;
  }

  async function resetAll(): Promise<BridgeResponse<AgentConfigDocument>> {
    return resetField('');
  }

  /** 从 AGENT_VALIDATION 消息里猜字段，供视图滚动定位。 */
  function guessField(message: string): string {
    const m = message.toLowerCase();
    if (m.includes('maxiterations') || message.includes('轮次')) return 'maxIterations';
    if (m.includes('execmaxtimeoutms') || message.includes('上限')) return 'execMaxTimeoutMs';
    return 'execDefaultTimeoutMs';
  }

  let unsub: (() => void) | null = null;
  function watchStorageChange() {
    if (unsub) return;
    unsub = haloEventBus.on('storage:changed', () => {
      void loadConfig();
    });
  }

  return {
    config,
    loading,
    saving,
    dirty,
    lastSavedHash,
    lastError,
    loadConfig,
    saveConfig,
    resetField,
    resetAll,
    touchDirty,
    markSaved,
    watchStorageChange,
  };
});
