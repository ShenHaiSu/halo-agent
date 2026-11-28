import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { haloEventBus } from '@shared/bridge/hostBridge';
import { useConfigStore } from '@/stores/configStore';

const bridge = vi.hoisted(() => ({
  getProvidersConfig: vi.fn(),
  getNetworkSettings: vi.fn(),
  inspectSystemProxy: vi.fn(),
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return {
    ...actual,
    getHostBridge: () => ({
      getNetworkSettings: bridge.getNetworkSettings,
      inspectSystemProxy: bridge.inspectSystemProxy,
      getProvidersConfig: bridge.getProvidersConfig,
    }),
  };
});

const docV1 = { activeProviderId: 'p1', activeModelId: 'm1', providers: [] };
const docV2 = { activeProviderId: 'p1', activeModelId: 'm2', providers: [] };

describe('configStore 订阅 config:providers-changed', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    bridge.getNetworkSettings.mockResolvedValue({ ok: true, data: null });
    bridge.inspectSystemProxy.mockResolvedValue({ ok: true, data: null });
    bridge.getProvidersConfig.mockResolvedValue({ ok: true, data: docV1 });
  });

  it('宿主广播后重新拉取并替换 providersDoc', async () => {
    const store = useConfigStore();
    await store.initialize();
    expect(store.providersDoc).toEqual(docV1);
    const callsBefore = bridge.getProvidersConfig.mock.calls.length;

    bridge.getProvidersConfig.mockResolvedValue({ ok: true, data: docV2 });
    haloEventBus.emitLocal('config:providers-changed', undefined);
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(bridge.getProvidersConfig.mock.calls.length).toBeGreaterThan(callsBefore);
    expect(store.providersDoc).toEqual(docV2);
  });
});
