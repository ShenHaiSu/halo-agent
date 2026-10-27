// src/Web.Client/src/stores/index.ts
// D4-8 store 注册入口（main.ts 在 mount 前调用）。
import { createPinia, type Pinia } from 'pinia';

let pinia: Pinia | null = null;

/** 创建（或复用）Pinia 实例。 */
export function createAppPinia(): Pinia {
  pinia ??= createPinia();
  return pinia;
}

export { useHostStore } from './host';
export { useUiStore } from './ui';
export { useThemeStore } from './theme';
export { useSessionsStore } from './sessions';
export { useSessionStore } from './session';
export { useMessagesStore } from './messages';
export { useStreamStore } from './stream';
export { usePermissionsStore } from './permissions';
export { dispatchFrames } from './eventBridge';
export { useEventPump } from './useEventPump';
