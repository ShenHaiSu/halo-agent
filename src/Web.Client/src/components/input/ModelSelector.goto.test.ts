// src/Web.Client/src/components/input/ModelSelector.goto.test.ts
// P27：跳设置必关浮窗（父层 funnel：openSettingsAndClose）。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import ModelSelector from './ModelSelector.vue';
import ModelSelectorPanel from './ModelSelectorPanel.vue';
import { useSessionStore } from '@/stores/session';
import { transport } from '@/transport';
import { openSettings } from '@/components/settings/SettingsApp.vue';

const mocks = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
  info: vi.fn(),
  warning: vi.fn(),
}));

vi.mock('@/composables/useToast', () => ({
  useToast: () => ({
    items: { value: [] },
    toasts: { value: [] },
    success: mocks.success,
    error: mocks.error,
    info: mocks.info,
    warning: mocks.warning,
    dismiss: vi.fn(),
    clear: vi.fn(),
  }),
}));

vi.mock('@/components/settings/SettingsApp.vue', () => ({
  openSettings: vi.fn(),
  default: {},
}));

function mockTransportWithProviders(providers: unknown[]) {
  vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
    if (method === 'settings.get') {
      return { activeProvider: '', activeModel: '', providers } as never;
    }
    if (method === 'state.snapshot') {
      return { agent: {}, session: { id: 's-1' } } as never;
    }
    throw new Error(`unexpected ${method}`);
  });
}

function mountSelector(pinia: ReturnType<typeof createPinia>) {
  return mount(ModelSelector, {
    global: {
      plugins: [pinia],
      stubs: {
        HButton: { template: '<button><slot /></button>' },
        HTooltip: { template: '<div><slot /></div>' },
        HaloIcon: { template: '<span />' },
      },
    },
  });
}

function popVisible(wrapper: ReturnType<typeof mount>): boolean {
  return wrapper.find('.modelbar-pop').exists();
}

async function openPop(wrapper: ReturnType<typeof mount>) {
  await wrapper.find('button').trigger('click');
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
}

beforeEach(() => {
  vi.restoreAllMocks();
  mocks.success.mockClear();
  mocks.error.mockClear();
  vi.mocked(openSettings).mockClear();
});

describe('ModelSelector 跳设置关浮窗', () => {
  it('浮窗开着点去模型设置 → 关浮窗 + openSettings(model)', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    mockTransportWithProviders([
      { id: 'p1', displayName: 'P1', baseUrl: 'https://x', apiKeySet: true, headersEnabled: true, headers: [], network: 'follow', responseStore: 'default', models: [{ id: 'm1', protocol: 'chat' }] },
    ]);
    const w = mountSelector(pinia);
    useSessionStore().applySnapshotAgent({ provider: 'p1', model: 'm1', configured: true });
    await openPop(w);
    expect(popVisible(w)).toBe(true);
    const panel = w.findComponent(ModelSelectorPanel);
    expect(panel.exists()).toBe(true);
    (panel.vm as unknown as { $emit: (e: string, ...a: unknown[]) => void }).$emit('openSettings');
    await nextTick();
    expect(popVisible(w)).toBe(false);
    expect(vi.mocked(openSettings)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(openSettings)).toHaveBeenCalledWith('model');
    w.unmount();
  });

  it('空 providers 点触发器 → 直接跳模型设置且不开窗', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    mockTransportWithProviders([]);
    const w = mountSelector(pinia);
    useSessionStore().applySnapshotAgent({ provider: '', model: '', configured: false });
    await openPop(w);
    expect(popVisible(w)).toBe(false);
    expect(vi.mocked(openSettings)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(openSettings)).toHaveBeenCalledWith('model');
    w.unmount();
  });
});
