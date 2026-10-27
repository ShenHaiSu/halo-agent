// src/Web.Client/src/stores/host.ts
// P5 扩展：isWin7 / dpiScale。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';
import { onHostEvent } from '@/bridge/events';
import { useMessagesStore } from './messages';
import type {
  HostSystemInfo,
  WindowState,
  EngineStatusPayload,
  FilesDroppedPayload,
} from '@/bridge/types';

export const useHostStore = defineStore('host', () => {
  const systemInfo = ref<HostSystemInfo | null>(null);
  const windowState = ref<WindowState | null>(null);
  const engineStatus = ref<EngineStatusPayload>({ state: 'starting' });
  const droppedFiles = ref<string[]>([]);
  const bridgeReady = ref(false);
  const lastNotice = ref<string | null>(null);

  const isWin7 = computed(() => {
    const v = (systemInfo.value as unknown as Record<string, unknown> | null)?.osVersion;
    if (typeof v === 'string') return /6\.1|Windows 7/i.test(v);
    if (typeof navigator !== 'undefined') return /Windows NT 6\.1/.test(navigator.userAgent);
    return false;
  });
  const dpiScale = computed(() =>
    typeof window !== 'undefined' ? window.devicePixelRatio || 1 : 1
  );

  let wired = false;
  function wireEvents(): void {
    if (wired) return;
    wired = true;
    onHostEvent('host.windowState', (p) => {
      windowState.value = p as WindowState;
    });
    onHostEvent('host.engineStatus', (p) => {
      const s = p as EngineStatusPayload;
      engineStatus.value = s;
      if (s.state === 'ready') useMessagesStore().loadSnapshot();
    });
    onHostEvent('host.filesDropped', (p) => {
      droppedFiles.value = (p as FilesDroppedPayload).paths ?? [];
    });
    onHostEvent('host.notice', (p) => {
      lastNotice.value = (p as { text?: string }).text ?? null;
    });
  }

  async function load(): Promise<void> {
    wireEvents();
    try {
      const [si, ws] = await Promise.all([
        transport.host.systemInfo(),
        transport.host.windowState(),
      ]);
      systemInfo.value = si;
      windowState.value = ws;
      bridgeReady.value = true;
    } catch (e) {
      console.warn('[host] 加载宿主信息失败', e);
    }
  }

  return {
    systemInfo,
    windowState,
    engineStatus,
    droppedFiles,
    bridgeReady,
    lastNotice,
    isWin7,
    dpiScale,
    load,
    wireEvents,
  };
});
