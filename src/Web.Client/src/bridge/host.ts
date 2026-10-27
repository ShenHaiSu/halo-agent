// src/Web.Client/src/bridge/host.ts
// D4-7 window.haloHost 的安全封装（11 方法，无宿主/调用失败时安全降级，不抛）。
import type { WindowState, HostSystemInfo } from './types';
import { hasHost, forceMock } from './bind';
import { createMockHost, type MockHost } from './mock/mockHost';

/**
 * window.haloHost 的安全封装。
 *
 * ============ 三层保护 ============
 *  ① 无宿主 / 强制 Mock 时走 Mock（不抛错）
 *  ② 真环境调用失败时降级到 Mock 的合理默认值（A-7.3：14 方法全不抛）
 *  ③ 方法缺失时给出清晰的中文日志（而不是 "undefined is not a function"）
 * ============ 为什么每个方法都单独写而不是 Proxy ============
 *  Proxy 会让类型推断失效，且错误信息指向 Proxy 内部。
 * ============ ============
 */
let mockHost: MockHost | null = null;

function getMock(): MockHost {
  mockHost ??= createMockHost();
  return mockHost;
}

function useMock(): boolean {
  return forceMock() || !hasHost();
}

function describeError(e: unknown): string {
  if (e instanceof Error) return e.message;
  if (typeof e === 'string') return e;
  if (e && typeof e === 'object' && 'message' in e)
    return String((e as { message: unknown }).message);
  return String(e);
}

/** 真环境调用，失败时记日志并降级到 fallback（不抛）。 */
async function callOrMock<T>(
  name: string,
  real: () => Promise<T>,
  fallback: () => Promise<T>
): Promise<T> {
  if (useMock()) return fallback();
  try {
    return await real();
  } catch (e) {
    console.warn(`[host] ${name} 失败，已降级到 Mock：${describeError(e)}`);
    return fallback();
  }
}

function requireMethod<K extends keyof NonNullable<Window['haloHost']>>(
  name: K
): NonNullable<Window['haloHost']>[K] {
  const host = window.haloHost;
  if (!host) throw new Error('宿主桥接不可用（window.haloHost 不存在）');
  const fn = host[name];
  if (typeof fn !== 'function') throw new Error(`宿主桥接缺少方法 ${String(name)}`);
  return (fn as (...args: never[]) => unknown).bind(host) as NonNullable<Window['haloHost']>[K];
}

export const hostBridge = {
  async windowMinimize(): Promise<void> {
    return callOrMock(
      'windowMinimize',
      () => requireMethod('windowMinimize')(),
      () => getMock().windowMinimize()
    );
  },

  async windowMaximizeOrRestore(): Promise<WindowState> {
    return callOrMock(
      'windowMaximizeOrRestore',
      () => requireMethod('windowMaximizeOrRestore')(),
      () => getMock().windowMaximizeOrRestore()
    );
  },

  async windowClose(): Promise<void> {
    return callOrMock(
      'windowClose',
      () => requireMethod('windowClose')(),
      () => getMock().windowClose()
    );
  },

  async windowState(): Promise<WindowState> {
    return callOrMock(
      'windowState',
      () => requireMethod('windowState')(),
      () => getMock().windowState()
    );
  },

  async systemInfo(): Promise<HostSystemInfo> {
    return callOrMock(
      'systemInfo',
      () => requireMethod('systemInfo')(),
      () => getMock().systemInfo()
    );
  },

  async openExternal(url: string): Promise<void> {
    return callOrMock(
      'openExternal',
      () => requireMethod('openExternal')(url),
      () => getMock().openExternal(url)
    );
  },

  async showItemInFolder(path: string): Promise<void> {
    return callOrMock(
      'showItemInFolder',
      () => requireMethod('showItemInFolder')(path),
      () => getMock().showItemInFolder(path)
    );
  },

  async copyText(text: string): Promise<void> {
    return callOrMock(
      'copyText',
      () => requireMethod('copyText')(text),
      () => getMock().copyText(text)
    );
  },

  async readClipboardImage(): Promise<string | null> {
    return callOrMock(
      'readClipboardImage',
      () => requireMethod('readClipboardImage')(),
      () => getMock().readClipboardImage()
    );
  },

  async toggleDevTools(): Promise<void> {
    return callOrMock(
      'toggleDevTools',
      () => requireMethod('toggleDevTools')(),
      () => getMock().toggleDevTools()
    );
  },

  async restartEngine(): Promise<void> {
    return callOrMock(
      'restartEngine',
      () => requireMethod('restartEngine')(),
      () => getMock().restartEngine()
    );
  },
};

export type HostBridge = typeof hostBridge;
