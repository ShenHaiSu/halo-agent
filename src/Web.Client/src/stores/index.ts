// src/Web.Client/src/stores/index.ts
// P5 store 注册入口。
import { createPinia, type Pinia } from 'pinia';

let pinia: Pinia | null = null;

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
export { useQueueStore } from './queue';
export { useUndoStore } from './undo';
export { dispatchFrames } from './eventBridge';
export { useEventPump } from './useEventPump';
