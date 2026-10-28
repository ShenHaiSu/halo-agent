// src/Web.Client/src/bridge/mock/mockHost.ts
// D4-7 无宿主时的 Mock（12 方法 + 内部测试钩子）。
import type { WindowState, HostSystemInfo } from '../types';

/**
 * 无宿主时的 Mock。
 *
 * ============ 为什么 Mock 要模拟到"像真的" ============
 *  纯浏览器开发时，界面必须看起来和宿主里一致（同样的 DPI、同样的窗口尺寸）。
 *  否则样式在浏览器里调好了，进宿主要重调一遍。
 * ============ ============
 */
export function createMockHost() {
  let maximized = false;
  let minimizeHandler: (() => void) | null = null;

  const state = (): WindowState => ({
    maximized,
    minimized: false,
    // 模拟一个 1280×820 的客户区
    width: Math.min(1280, window.innerWidth),
    height: Math.min(820, window.innerHeight),
    dpi: Math.round(window.devicePixelRatio * 96),
    titleBarHeight: 40,
  });

  return {
    async windowMinimize(): Promise<void> {
      minimizeHandler?.();
      console.debug('[mock] windowMinimize（浏览器里无效果）');
    },
    async windowDragStart(): Promise<void> {
      // 浏览器里没有窗口可拖：空操作（B 方案仅宿主有效）。
      console.debug('[mock] windowDragStart（浏览器里无效果）');
    },

    async windowMaximizeOrRestore(): Promise<WindowState> {
      maximized = !maximized;
      // 浏览器里用 Fullscreen API 模拟
      try {
        if (maximized) await document.documentElement.requestFullscreen?.();
        else if (document.fullscreenElement) await document.exitFullscreen?.();
      } catch {
        /* 忽略 */
      }
      return state();
    },

    async windowClose(): Promise<void> {
      console.debug('[mock] windowClose（浏览器里不关闭）');
    },

    async windowState(): Promise<WindowState> {
      return state();
    },

    async systemInfo(): Promise<HostSystemInfo> {
      return {
        osDescription: navigator.userAgent,
        is64BitOs: true,
        is64BitProcess: true,
        isWindows7: false,
        cefVersion:
          'mock (Chrome ' + (navigator.userAgent.match(/Chrome\/(\d+)/)?.[1] ?? '?') + ')',
        hostVersion: '0.4.0-mock',
        dpi: Math.round(window.devicePixelRatio * 96),
        isDev: true,
        isFirstRun: false,
        dataDir: '(mock) C:\\Users\\you\\.haloagent',
        cwd: '(mock) C:\\work',
        gpuMode: 'Mock',
        screens: [
          {
            bounds: [0, 0, window.screen.width, window.screen.height],
            deviceName: '(mock) DISPLAY1',
            primary: true,
          },
        ],
      };
    },

    async openExternal(url: string): Promise<void> {
      window.open(url, '_blank', 'noopener,noreferrer');
    },

    async showItemInFolder(path: string): Promise<void> {
      console.debug('[mock] showItemInFolder', path);
    },

    async copyText(text: string): Promise<void> {
      await navigator.clipboard.writeText(text);
    },

    async readClipboardImage(): Promise<string | null> {
      try {
        const items = await navigator.clipboard.read?.();
        for (const item of items ?? []) {
          const type = item.types.find((t) => t.startsWith('image/'));
          if (!type) continue;
          const blob = await item.getType(type);
          return await blobToBase64(blob);
        }
      } catch {
        /* 浏览器可能不允许 */
      }
      return null;
    },

    async toggleDevTools(): Promise<void> {
      console.debug('[mock] toggleDevTools（浏览器里请用 F12）');
    },

    async restartEngine(): Promise<void> {
      console.debug('[mock] restartEngine');
    },

    /** 仅供 Mock 内部模拟 */
    __setMinimizeHandler(fn: () => void) {
      minimizeHandler = fn;
    },
  };
}

export type MockHost = ReturnType<typeof createMockHost>;

function blobToBase64(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const s = String(reader.result ?? '');
      // 去掉 "data:xxx;base64," 前缀
      const comma = s.indexOf(',');
      resolve(comma >= 0 ? s.slice(comma + 1) : s);
    };
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(blob);
  });
}
