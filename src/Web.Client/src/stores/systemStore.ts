import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import {
  getHostBridge,
  haloEventBus,
  type SystemRuntimeInfo,
  type StorageConfigInfo,
  type StorageMigrationResult,
  type BridgeResponse,
} from '@/bridge/hostBridge';

export const useSystemStore = defineStore('system', () => {
  const systemInfo = ref<SystemRuntimeInfo | null>(null);
  const storageConfig = ref<StorageConfigInfo | null>(null);
  const loading = ref(false);
  const migrating = ref(false);
  const migrationPercent = ref(0);
  const migrationStatusText = ref('');
  const themeMode = ref<'dark' | 'light'>('dark');
  const recentHostEvents = ref<Array<{ type: string; summary: string; timestamp: number }>>([]);

  const isNativeHost = computed(() => typeof window !== 'undefined' && !!window.CefSharp);
  const currentProtocol = computed(() => (typeof window !== 'undefined' ? window.location.href : ''));

  const diskFreeGbText = computed(() => {
    const mb = storageConfig.value?.diskFreeSpaceMb ?? 0;
    return (mb / 1024).toFixed(1);
  });

  function applyThemeClass(mode: 'dark' | 'light') {
    themeMode.value = mode;
    if (typeof document !== 'undefined') {
      const root = document.documentElement;
      if (mode === 'light') {
        root.classList.remove('dark');
        root.classList.add('light');
      } else {
        root.classList.remove('light');
        root.classList.add('dark');
      }
    }
  }

  function toggleTheme() {
    applyThemeClass(themeMode.value === 'dark' ? 'light' : 'dark');
  }

  async function refreshAll(): Promise<void> {
    loading.value = true;
    try {
      const bridge = getHostBridge();
      const [sysRes, storageRes] = await Promise.all([
        bridge.getSystemInfo(),
        bridge.getStorageConfig(),
      ]);

      if (sysRes.ok && sysRes.data) {
        systemInfo.value = sysRes.data;
      }
      if (storageRes.ok && storageRes.data) {
        storageConfig.value = storageRes.data;
      }
    } finally {
      loading.value = false;
    }
  }

  async function selectFolder(initialPath?: string): Promise<string | null> {
    const bridge = getHostBridge();
    const res = await bridge.selectFolderDialog(initialPath ?? storageConfig.value?.dataRootPath);
    if (res.ok && res.data) {
      return res.data;
    }
    return null;
  }

  async function changeStorageRoot(
    newPath: string,
    migrateExistingData: boolean
  ): Promise<BridgeResponse<StorageMigrationResult>> {
    migrating.value = true;
    migrationPercent.value = 5;
    migrationStatusText.value = '正在校验并准备目标目录...';

    try {
      const bridge = getHostBridge();
      const res = await bridge.changeStoragePath(newPath, migrateExistingData);
      await refreshAll();
      return res;
    } finally {
      migrating.value = false;
    }
  }

  async function resetToDefaultCDrive(
    migrateExistingData = true
  ): Promise<BridgeResponse<StorageMigrationResult>> {
    migrating.value = true;
    migrationPercent.value = 10;
    migrationStatusText.value = '正在恢复默认 C:\\HaloAgentData...';

    try {
      const bridge = getHostBridge();
      const res = await bridge.resetStoragePathToDefault(migrateExistingData);
      await refreshAll();
      return res;
    } finally {
      migrating.value = false;
    }
  }

  async function openInExplorer(targetPath?: string): Promise<BridgeResponse<boolean>> {
    const bridge = getHostBridge();
    return bridge.openPathInExplorer(targetPath || storageConfig.value?.dataRootPath || 'C:\\HaloAgentData');
  }

  async function cleanupLogs(retentionDays = 14): Promise<BridgeResponse<number>> {
    const bridge = getHostBridge();
    const res = await bridge.cleanupExpiredLogs(retentionDays);
    await refreshAll();
    return res;
  }

  async function writeTestLogEntry(channel: 'app' | 'llm' | 'tool', message: string): Promise<void> {
    const bridge = getHostBridge();
    await bridge.writeDiagnosticLog(channel, message);
    await refreshAll();
  }

  function setupEventListeners() {
    haloEventBus.on<{ percent: number; message: string; targetPath: string }>(
      'storage:migration-progress',
      (payload, timestamp) => {
        migrationPercent.value = payload.percent;
        migrationStatusText.value = payload.message;
        recentHostEvents.value.unshift({
          type: 'storage:migration-progress',
          summary: `[${payload.percent}%] ${payload.message}`,
          timestamp,
        });
        if (recentHostEvents.value.length > 20) {
          recentHostEvents.value.length = 20;
        }
      }
    );

    haloEventBus.on<{ oldDataRootPath: string; newDataRootPath: string; migratedFileCount: number }>(
      'storage:changed',
      (payload, timestamp) => {
        recentHostEvents.value.unshift({
          type: 'storage:changed',
          summary: `${payload.oldDataRootPath} -> ${payload.newDataRootPath} (${payload.migratedFileCount} files)`,
          timestamp,
        });
        if (recentHostEvents.value.length > 20) {
          recentHostEvents.value.length = 20;
        }
      }
    );
  }

  return {
    systemInfo,
    storageConfig,
    loading,
    migrating,
    migrationPercent,
    migrationStatusText,
    themeMode,
    recentHostEvents,
    isNativeHost,
    currentProtocol,
    diskFreeGbText,
    applyThemeClass,
    toggleTheme,
    refreshAll,
    selectFolder,
    changeStorageRoot,
    resetToDefaultCDrive,
    openInExplorer,
    cleanupLogs,
    writeTestLogEntry,
    setupEventListeners,
  };
});
