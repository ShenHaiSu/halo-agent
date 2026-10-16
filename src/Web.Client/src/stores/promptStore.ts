import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  getHostBridge,
  type PromptConfigDocument,
  type PromptPreviewDto,
  type PromptDiagnosticsDto,
  type BridgeResponse,
} from '@/bridge/hostBridge';

export const usePromptStore = defineStore('prompt', () => {
  const config = ref<PromptConfigDocument | null>(null);
  const preview = ref<PromptPreviewDto | null>(null);
  const diagnostics = ref<PromptDiagnosticsDto | null>(null);
  const loading = ref(false);
  const saving = ref(false);
  const previewing = ref(false);
  const lastSavedHash = ref('');
  const previewStale = ref(false);

  const dirty = computed(() => {
    if (!config.value) return false;
    return JSON.stringify(config.value) !== lastSavedHash.value;
  });

  function markClean() {
    lastSavedHash.value = config.value ? JSON.stringify(config.value) : '';
    previewStale.value = false;
  }

  function touchDirty() {
    previewStale.value = true;
  }

  async function loadConfig(): Promise<void> {
    loading.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.getPromptConfig();
      if (res.ok && res.data) {
        config.value = res.data;
        markClean();
      }
    } finally {
      loading.value = false;
    }
  }

  async function saveConfig(): Promise<BridgeResponse<PromptConfigDocument>> {
    if (!config.value) throw new Error('no-config');
    saving.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.savePromptConfig(config.value);
      if (res.ok && res.data) {
        config.value = res.data;
        markClean();
      }
      return res;
    } finally {
      saving.value = false;
    }
  }

  async function resetBlock(blockId?: string): Promise<BridgeResponse<PromptConfigDocument>> {
    const bridge = getHostBridge();
    const res = await bridge.resetPromptConfig(blockId);
    if (res.ok && res.data) {
      config.value = res.data;
      markClean();
    }
    return res;
  }

  async function resetAll(): Promise<BridgeResponse<PromptConfigDocument>> {
    return resetBlock(undefined);
  }

  async function refreshPreview(workspaceId: string): Promise<void> {
    previewing.value = true;
    try {
      const bridge = getHostBridge();
      const res = await bridge.previewPrompt(workspaceId || '');
      if (res.ok && res.data) {
        preview.value = res.data;
        previewStale.value = false;
      }
    } finally {
      previewing.value = false;
    }
  }

  async function loadDiagnostics(workspaceId: string, sessionId?: string): Promise<void> {
    try {
      const bridge = getHostBridge();
      const res = await bridge.getPromptDiagnostics(workspaceId || '', sessionId);
      if (res.ok && res.data) {
        diagnostics.value = res.data;
        if (!preview.value) preview.value = res.data.currentPreview;
      }
    } catch {
      /* 诊断失败不阻断主流程 */
    }
  }

  /** 前端轻量 token 估算（示意，标注 ≈，真值以后端 preview 为准）。 */
  function estimateTokensLocal(text: string): number {
    if (!text) return 0;
    let ascii = 0;
    let cjk = 0;
    let other = 0;
    for (const ch of text) {
      const c = ch.codePointAt(0) || 0;
      if (c <= 127) ascii++;
      else if (c >= 0x4e00 && c <= 0x9fff) cjk++;
      else other++;
    }
    return Math.max(1, Math.ceil(ascii / 3.6 + cjk * 1.55 + other * 1.2));
  }

  return {
    config,
    preview,
    diagnostics,
    loading,
    saving,
    previewing,
    dirty,
    previewStale,
    loadConfig,
    saveConfig,
    resetBlock,
    resetAll,
    refreshPreview,
    loadDiagnostics,
    markClean,
    touchDirty,
    estimateTokensLocal,
  };
});
