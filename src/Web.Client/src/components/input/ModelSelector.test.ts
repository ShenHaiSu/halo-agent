// src/Web.Client/src/components/input/ModelSelector.test.ts
// fix-model-switch F4：onSelect 成功口径以快照为准（toast 成功 + 面板关闭 vs 未生效/失败 + 面板保持打开）。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import ModelSelector from './ModelSelector.vue';
import ModelSelectorPanel from './ModelSelectorPanel.vue';
import { useSessionStore } from '@/stores/session';
import { transport } from '@/transport';

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

function mockTransportFor(opts: {
  snapshotAgent: Record<string, unknown>;
  setActiveThrow?: string;
  snapshotThrow?: string;
}) {
  vi.spyOn(transport, 'request').mockImplementation(async (method: string) => {
    if (method === 'settings.setActive') {
      if (opts.setActiveThrow) throw new Error(opts.setActiveThrow);
      return { ok: true } as never;
    }
    if (method === 'state.snapshot') {
      if (opts.snapshotThrow) throw new Error(opts.snapshotThrow);
      return { agent: opts.snapshotAgent, session: { id: 's-1' } } as never;
    }
    if (method === 'settings.get') {
      return {
        activeProvider: '',
        activeModel: '',
        providers: [
          { id: 'p1', displayName: 'P1', baseUrl: 'https://x', apiKeySet: true, headersEnabled: true, headers: [], network: 'follow', models: [{ id: 'm1', protocol: 'chat' }] },
        ],
      } as never;
    }
    throw new Error(`unexpected ${method}`);
  });
}

async function openThenSelect(wrapper: ReturnType<typeof mount>, providerId: string, modelId: string) {
  // 点触发器开窗（toggle 内会 loadTree，settings.get 已 mock）
  await wrapper.find('button').trigger('click');
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
  const panel = wrapper.findComponent(ModelSelectorPanel);
  expect(panel.exists()).toBe(true);
  (panel.vm as unknown as { $emit: (e: string, ...a: unknown[]) => void }).$emit('select', providerId, modelId);
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
}

function popVisible(wrapper: ReturnType<typeof mount>): boolean {
  return wrapper.find('.modelbar-pop').exists();
}

beforeEach(() => {
  vi.restoreAllMocks();
  mocks.success.mockClear();
  mocks.error.mockClear();
});

describe('ModelSelector onSelect 快照确认口径', () => {
  it('setActive ok + snapshot configured → toast.success、面板关闭', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    mockTransportFor({
      snapshotAgent: { provider: 'p1', providerDisplay: 'P1', model: 'm1', modelDisplay: 'M1', configured: true },
    });
    const w = mount(ModelSelector, {
      global: {
        plugins: [pinia],
        stubs: {
          HButton: { template: '<button><slot /></button>' },
          HTooltip: { template: '<div><slot /></div>' },
          HaloIcon: { template: '<span />' },
        },
      },
    });
    const session = useSessionStore();
    session.applySnapshotAgent({ provider: 'p0', model: 'm0', configured: true });
    await openThenSelect(w, 'p1', 'm1');
    expect(session.configured).toBe(true);
    expect(session.provider).toBe('p1');
    expect(session.model).toBe('m1');
    expect(mocks.success).toHaveBeenCalledTimes(1);
    expect(mocks.error).not.toHaveBeenCalled();
    expect(popVisible(w)).toBe(false);
    w.unmount();
  });

  it('setActive ok + snapshot 仍 unconfigured → toast.error（未生效）、面板保持打开', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    mockTransportFor({
      snapshotAgent: { provider: 'p0', providerDisplay: 'P0', model: 'm0', modelDisplay: 'M0', configured: false },
    });
    const w = mount(ModelSelector, {
      global: {
        plugins: [pinia],
        stubs: {
          HButton: { template: '<button><slot /></button>' },
          HTooltip: { template: '<div><slot /></div>' },
          HaloIcon: { template: '<span />' },
        },
      },
    });
    const session = useSessionStore();
    session.applySnapshotAgent({ provider: 'p0', model: 'm0', configured: true });
    await openThenSelect(w, 'p1', 'm1');
    expect(mocks.success).not.toHaveBeenCalled();
    expect(mocks.error).toHaveBeenCalledTimes(1);
    expect(String(mocks.error.mock.calls[0]?.[0] ?? '')).toContain('未生效');
    expect(popVisible(w)).toBe(true);
    w.unmount();
  });

  it('setActive 抛错 → toast.error、面板保持打开', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    mockTransportFor({
      snapshotAgent: { provider: 'p0', model: 'm0', configured: true },
      setActiveThrow: 'state.busy: 有回合正在执行',
    });
    const w = mount(ModelSelector, {
      global: {
        plugins: [pinia],
        stubs: {
          HButton: { template: '<button><slot /></button>' },
          HTooltip: { template: '<div><slot /></div>' },
          HaloIcon: { template: '<span />' },
        },
      },
    });
    const session = useSessionStore();
    session.applySnapshotAgent({ provider: 'p0', model: 'm0', configured: true });
    await openThenSelect(w, 'p1', 'm1');
    expect(mocks.success).not.toHaveBeenCalled();
    expect(mocks.error).toHaveBeenCalledTimes(1);
    expect(String(mocks.error.mock.calls[0]?.[0] ?? '')).toContain('切换失败');
    expect(popVisible(w)).toBe(true);
    w.unmount();
  });
});
