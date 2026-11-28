import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { defineComponent, h } from 'vue';
import { NMessageProvider } from 'naive-ui';
import ProviderModelCatalogDialog from './ProviderModelCatalogDialog.vue';
import { useModelCatalogStore } from '@/stores/modelCatalogStore';

const bridge = vi.hoisted(() => ({
  importDiscoveredModels: vi.fn(async () => ({
    ok: true,
    data: { activeProviderId: 'provider-openai', activeModelId: 'glm-5.3', providers: [] },
  })),
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return {
    ...actual,
    getHostBridge: () => ({ importDiscoveredModels: bridge.importDiscoveredModels }),
  };
});

function mountDialog() {
  const pinia = createPinia();
  setActivePinia(pinia);
  const store = useModelCatalogStore();
  store.snapshot = {
    providerId: 'provider-openai',
    modelsUrl: 'https://api.openai.com/v1/models',
    effectiveProxySummary: 'DIRECT (No Proxy)',
    elapsedMs: 12,
    models: [
      { id: 'glm-5.3', ownedBy: 'openai', created: 1626777600, state: 'new', existsEnabled: false },
      { id: 'gpt-4o', ownedBy: 'openai', created: 1626777600, state: 'existing', existsEnabled: true },
      { id: 'gpt-6-astra', ownedBy: 'openai', created: 1626777600, state: 'ignored', existsEnabled: false },
    ],
  };
  const Host = defineComponent({
    render() {
      return h(NMessageProvider, null, {
        default: () => h(ProviderModelCatalogDialog, { show: true, providerName: 'OpenAI' }),
      });
    },
  });
  const wrapper = mount(Host, {
    global: { plugins: [pinia] },
  });
  return { wrapper, store };
}

describe('ProviderModelCatalogDialog', () => {
  it('三态标签渲染齐备', () => {
    const { wrapper } = mountDialog();
    const text = wrapper.text();
    expect(text).toContain('glm-5.3');
    expect(text).toContain('新增');
    expect(text).toContain('已存在');
    expect(text).toContain('已忽视');
  });

  it('引入按钮写通路已解禁', () => {
    const { wrapper } = mountDialog();
    const html = wrapper.html();
    expect(html).toContain('引入并启用选中');
    expect(html).toContain('引入但不启用选中');
    expect(html).toContain('catalog-import-enabled');
    expect(html).toContain('catalog-import-disabled');
    expect(html).toContain('忽视');
    expect(html).toContain('设为默认');
  });

  it('搜索可过滤列表', async () => {
    const { wrapper, store } = mountDialog();
    store.search = 'glm';
    await wrapper.vm.$nextTick();
    expect(wrapper.text()).toContain('glm-5.3');
    expect(wrapper.text()).not.toContain('gpt-4o');
  });

  it('引入成功后行翻为已存在并广播 imported', async () => {
    const { wrapper, store } = mountDialog();
    store.providerId = 'provider-openai';
    store.toggle('glm-5.3');
    await wrapper.vm.$nextTick();

    await wrapper.find('[data-testid="catalog-import-enabled"]').trigger('click');
    await new Promise((resolve) => setTimeout(resolve, 0));
    await wrapper.vm.$nextTick();

    expect(store.snapshot?.models.find((m) => m.id === 'glm-5.3')?.state).toBe('existing');
    expect(wrapper.findComponent(ProviderModelCatalogDialog).emitted('imported')).toBeTruthy();
  });
});
