// src/Web.Client/src/components/input/ModeSelector.goto.test.ts
// P27：跳设置必关浮窗（父层 funnel：openSettingsAndClose）。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import ModeSelector from './ModeSelector.vue';
import ModeSelectorPanel from './ModeSelectorPanel.vue';
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

beforeEach(() => {
  vi.restoreAllMocks();
  mocks.success.mockClear();
  mocks.error.mockClear();
  vi.mocked(openSettings).mockClear();
});

describe('ModeSelector 跳设置关浮窗', () => {
  it('浮窗开着点去权限设置 → 关浮窗 + openSettings(permissions)', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const w = mount(ModeSelector, {
      global: {
        plugins: [pinia],
        stubs: {
          HButton: { template: '<button><slot /></button>' },
          HTooltip: { template: '<div><slot /></div>' },
          HaloIcon: { template: '<span />' },
        },
      },
    });
    await w.find('button').trigger('click');
    await nextTick();
    expect(w.find('.modebar-pop').exists()).toBe(true);
    const panel = w.findComponent(ModeSelectorPanel);
    expect(panel.exists()).toBe(true);
    (panel.vm as unknown as { $emit: (e: string, ...a: unknown[]) => void }).$emit('openSettings');
    await nextTick();
    expect(w.find('.modebar-pop').exists()).toBe(false);
    expect(vi.mocked(openSettings)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(openSettings)).toHaveBeenCalledWith('permissions');
    w.unmount();
  });
});
