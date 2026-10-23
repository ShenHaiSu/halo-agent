/** need22-P3 G5：Mock 桥接（四模块之 mock，仅开发用，显式标记，禁止静默）。
 * Mock 生效时 UI 必须展示红色横幅 MOCK_BANNER_TEXT + 重试按钮（见 App.vue 横幅）。 */
import { MOCK_BANNER_TEXT } from '@shared/bridge/protocol/types';

export { MOCK_BANNER_TEXT };

/** 是否处于 Mock 模式（proxy 在 negotiate/Bind 失败后显式设置，localStorage 可覆盖标记便于联调复现）。 */
const MOCK_FLAG_KEY = 'halo-bridge-mock';

export function isMockMode(): boolean {
  try {
    if (typeof sessionStorage !== 'undefined' && sessionStorage.getItem(MOCK_FLAG_KEY) === '1') return true;
  } catch (e) {
    console.warn('[bridge-mock] flag read failed:', e);
  }
  return (window as unknown as { __haloBridgeMock?: boolean }).__haloBridgeMock === true;
}

export function setMockMode(on: boolean): void {
  try {
    (window as unknown as { __haloBridgeMock?: boolean }).__haloBridgeMock = on;
    if (typeof sessionStorage !== 'undefined') {
      if (on) sessionStorage.setItem(MOCK_FLAG_KEY, '1');
      else sessionStorage.removeItem(MOCK_FLAG_KEY);
    }
  } catch (e) {
    console.warn('[bridge-mock] flag write failed:', e);
  }
}

/** Mock 横幅是否可见 = Mock 模式。调用方（App.vue）据此渲染红色横幅 + 重试按钮。 */
export function shouldShowMockBanner(): boolean {
  return isMockMode();
}

import type { HostBridgeApi } from '@shared/bridge/protocol/types';
import { createMockState } from './mockState';
import { mockSystemMethods } from './mockSystem';
import { mockWorkspaceMethods } from './mockWorkspace';

/** need22-P5 J4：Mock 工厂（原 hostBridge.ts createBrowserMockBridge 整体搬迁；状态→MockState，方法→system/workspace 两组组合）。 */
export function createBrowserMockBridge(): HostBridgeApi {
  const s = createMockState();
  const bridge = {
    ...mockSystemMethods(s),
    ...mockWorkspaceMethods(s),
  } as HostBridgeApi;
  s.self = bridge;
  return bridge;
}
