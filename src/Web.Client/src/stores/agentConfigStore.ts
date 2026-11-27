import { defineStore } from 'pinia';
import { computed, ref, type ComputedRef, type Ref } from 'vue';
import { getHostBridge, haloEventBus, type AgentConfigDocument, type BridgeResponse } from '@shared/bridge/hostBridge';

/** 智能体行为配置状态：保存基线、字段脏态与外部变更保护。 */
export const useAgentConfigStore = defineStore('agentConfig', () => {
  const config = ref<AgentConfigDocument | null>(null);
  const savedConfig: Ref<AgentConfigDocument | null> = ref(null);
  const loading = ref(false);
  const saving = ref(false);
  const lastSavedHash = ref('');
  const lastError = ref<{ field: string; message: string } | null>(null);
  const externalChangePending: Ref<boolean> = ref(false);

  const dirtyFields: ComputedRef<string[]> = computed(() => {
    if (!config.value || !savedConfig.value) return [];
    const current = config.value as unknown as Record<string, unknown>;
    const saved = savedConfig.value as unknown as Record<string, unknown>;
    const keys = new Set([...Object.keys(current), ...Object.keys(saved)]);
    return [...keys].filter((key) => JSON.stringify(current[key]) !== JSON.stringify(saved[key]));
  });

  const dirty = computed(() => dirtyFields.value.length > 0);

  function cloneConfig(document: AgentConfigDocument): AgentConfigDocument {
    return JSON.parse(JSON.stringify(document)) as AgentConfigDocument;
  }

  function markSaved(): void {
    savedConfig.value = config.value ? cloneConfig(config.value) : null;
    lastSavedHash.value = config.value ? JSON.stringify(config.value) : '';
    lastError.value = null;
    externalChangePending.value = false;
  }

  function discardChanges(): void {
    if (savedConfig.value) {
      config.value = cloneConfig(savedConfig.value);
    }
    lastError.value = null;
    externalChangePending.value = false;
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
    // 子代理轮次字段包含最大轮次字段名，必须先判，避免误定位。
    if (m.includes('submaxparallel') || message.includes('并发')) return 'subMaxParallel';
    if (m.includes('submaxiterations') || message.includes('子轮次')) return 'subMaxIterations';
    if (m.includes('subagentenabled') || message.includes('总开关')) return 'subAgentEnabled';
    if (m.includes('suballow') || message.includes('权限')) return 'subPerms';
    if (m.includes('turntimeout') || message.includes('单轮最大时间') || message.includes('单轮总体超时')) return 'turnTimeoutMs';
    if (m.includes('maxiterations') || message.includes('轮次')) return 'maxIterations';
    if (m.includes('execmaxtimeoutms') || message.includes('上限')) return 'execMaxTimeoutMs';
    return 'execDefaultTimeoutMs';
  }

  let unsub: (() => void) | null = null;
  function watchStorageChange() {
    if (unsub) return;
    unsub = haloEventBus.on('storage:changed', () => {
      if (dirty.value) {
        externalChangePending.value = true;
        return;
      }
      void loadConfig();
    });
  }

  return {
    config,
    savedConfig,
    loading,
    saving,
    dirty,
    dirtyFields,
    externalChangePending,
    lastSavedHash,
    lastError,
    loadConfig,
    saveConfig,
    resetField,
    resetAll,
    touchDirty,
    discardChanges,
    markSaved,
    watchStorageChange,
  };
});
