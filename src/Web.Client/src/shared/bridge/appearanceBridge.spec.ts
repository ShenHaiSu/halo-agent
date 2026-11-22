/** 外观配置桥契约：方法暴露、老后端降级、前后端 DTO 字段对齐（防止两端漂移）。 */
import { describe, expect, it } from 'vitest';

import { DEFAULT_APPEARANCE } from '@shared/ui/appearance';
import { createHostBridgeApi } from './api';
import type { AppearanceSettingsDto } from './protocol/types';
import { BRIDGE_VERSION } from './protocol/version';
import type { BridgeTransport } from './transport/transport';

function makeTransport(supports: boolean): BridgeTransport {
  const call = async <T>(method: string, ..._args: unknown[]): Promise<T> => {
    const payload = method === 'getAppearance' ? { ok: true, data: { ...DEFAULT_APPEARANCE } } : { ok: true, data: true };
    return payload as T;
  };
  return {
    call,
    supports: () => supports,
    version: async () => BRIDGE_VERSION,
  };
}

describe('外观配置桥契约', () => {
  it('createHostBridgeApi 暴露 getAppearance/setAppearance 且可调用', async () => {
    const api = createHostBridgeApi(makeTransport(true));
    expect(typeof api.getAppearance).toBe('function');
    expect(typeof api.setAppearance).toBe('function');
    expect((await api.getAppearance()).data?.themeColor).toBe('#3b82f6');
    expect((await api.setAppearance('{}')).ok).toBe(true);
  });

  it('老后端缺少方法时降级为 APPEARANCE_UNSUPPORTED（不升 BRIDGE_VERSION、不阻断）', async () => {
    const api = createHostBridgeApi(makeTransport(false));
    expect(await api.getAppearance()).toMatchObject({
      ok: false,
      code: 'APPEARANCE_UNSUPPORTED',
    });
    expect(await api.setAppearance('{}')).toMatchObject({
      ok: false,
      code: 'APPEARANCE_UNSUPPORTED',
    });
  });

  it('AppearanceSettingsDto 与 DEFAULT_APPEARANCE 字段集合一致（防止两端漂移）', () => {
    const dtoKeys: Array<keyof AppearanceSettingsDto> = [
      'themeMode',
      'themeColor',
      'fontSize',
      'chineseFont',
      'englishFont',
      'monoFont',
      'chatWidthMode',
      'sendKeyMode',
    ];
    expect([...dtoKeys].sort()).toEqual(Object.keys(DEFAULT_APPEARANCE).sort());
  });
});
