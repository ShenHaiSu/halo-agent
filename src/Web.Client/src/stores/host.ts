// src/Web.Client/src/stores/host.ts
// D4-8 宿主状态 store（全应用除 eventBridge 外唯一允许订阅宿主事件的地方）。
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { transport } from '@/transport';
import { onHostEvent } from '@/bridge/events';
import { useMessagesStore } from './messages';
import type {
  HostSystemInfo,
  WindowState,
  EngineStatusPayload,
  FilesDroppedPayload,
} from '@/bridge/types';

/**
 * 宿主状态 store。
 *
 * ============ 两类数据来源 ============
 *  ① 主动拉取（load）：systemInfo / windowState —— 挂载时一次
 *  ② 事件推送（onHostEvent）：windowState / engineStatus / filesDropped
 *     —— 由宿主用 EvaluateScriptAsync 推来
 * ============ 为什么 engineStatus 要放这里 ============
 *  它影响全局 UI（标题栏状态、是否禁止发送），不属于某个会话。
 * ============ ============
 */
export const useHostStore = defineStore('host', () => {
  const systemInfo = ref<HostSystemInfo | null>(null);
  const windowState = ref<WindowState | null>(null);
  const engineStatus = ref<EngineStatusPayload>({ state: 'starting' });
  const droppedFiles = ref<string[]>([]);
  const bridgeReady = ref(false);
  const lastNotice = ref<string | null>(null);

  let wired = false;

  /** 绑定宿主事件（只做一次） */
  function wireEvents() {
    if (wired) return;
    wired = true;

    onHostEvent('host.windowState', (p) => {
      windowState.value = p as WindowState;
    });

    onHostEvent('host.engineStatus', (p) => {
      const s = p as EngineStatusPayload;
      engineStatus.value = s;
      // 引擎重启后前端状态可能不同步，强制重载快照
      if (s.state === 'ready') {
        useMessagesStore().loadSnapshot();
      }
    });

    onHostEvent('host.filesDropped', (p) => {
      droppedFiles.value = (p as FilesDroppedPayload).paths ?? [];
    });

    onHostEvent('host.notice', (p) => {
      const n = p as { text?: string };
      lastNotice.value = n.text ?? null;
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
    load,
    wireEvents,
  };
});
