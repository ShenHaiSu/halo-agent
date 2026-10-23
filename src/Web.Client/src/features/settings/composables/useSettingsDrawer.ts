/**
 * need23：设置中心抽屉的关闭通道。
 * SettingsCenterDrawer provide → AgentBehaviorView inject。
 * 场景：卡片动作需要把主工作台让出来（如"进入子会话"），否则抽屉会盖住结果。
 */
import { inject, provide, type InjectionKey } from 'vue';

export interface SettingsDrawerBridge {
  /** 关闭设置中心抽屉。 */
  close(): void;
}

export const SETTINGS_DRAWER_KEY: InjectionKey<SettingsDrawerBridge> = Symbol('halo.settingsDrawer');

export function provideSettingsDrawer(bridge: SettingsDrawerBridge): void {
  provide(SETTINGS_DRAWER_KEY, bridge);
}

/** 兜底：未 provide 时返回 no-op（单测/独立渲染不崩）。 */
export function useSettingsDrawer(): SettingsDrawerBridge {
  const bridge = inject(SETTINGS_DRAWER_KEY, null);
  if (bridge) return bridge;
  return { close() {} };
}
