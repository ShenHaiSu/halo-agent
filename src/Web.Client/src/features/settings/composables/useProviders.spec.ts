import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import type { ModelConfig, ProviderConfig } from '@shared/bridge/hostBridge';
import { useProviders } from './useProviders';

const message = vi.hoisted(() => ({ warning: vi.fn(), success: vi.fn(), error: vi.fn() }));
const bridge = vi.hoisted(() => ({
  upsertProvider: vi.fn(async () => ({
    ok: true,
    data: { activeProviderId: 'p1', activeModelId: 'gpt-4o', providers: [] },
  })),
  previewProviderResolution: vi.fn(async () => ({ ok: false, data: null })),
}));

vi.mock('naive-ui', async (importOriginal) => {
  const actual = await importOriginal<typeof import('naive-ui')>();
  return { ...actual, useMessage: () => message };
});

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return {
    ...actual,
    supportsHostBridgeMethod: () => true,
    getHostBridge: () => ({
      upsertProvider: bridge.upsertProvider,
      previewProviderResolution: bridge.previewProviderResolution,
    }),
  };
});

function model(id: string): ModelConfig {
  return {
    id,
    displayName: '',
    protocol: 'chat',
    maxContextTokens: 128000,
    maxOutputTokens: 4096,
    enabled: true,
  } as ModelConfig;
}

function provider(models: ModelConfig[]): ProviderConfig {
  return {
    id: 'p1',
    name: '自定义 OpenAI 兼容服务商',
    enabled: true,
    baseUrl: 'https://api.openai.com/v1',
    apiKeyEncrypted: '',
    proxyMode: 'Inherit',
    customProxy: null,
    enableCustomHeaders: false,
    customHeaders: [],
    models,
  } as ProviderConfig;
}

function setupProviders(): ReturnType<typeof useProviders> {
  let api!: ReturnType<typeof useProviders>;
  const Comp = defineComponent({
    setup() {
      api = useProviders();
      return () => null;
    },
  });
  mount(Comp);
  return api;
}

describe('useProviders 模型行防呆', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    message.warning.mockClear();
    bridge.upsertProvider.mockClear();
  });

  it('新增模型行使用空 ID 占位，不再生成 model-N', () => {
    const api = setupProviders();
    api.replaceEditing(provider([model('gpt-4o')]));
    api.addModelRow();
    const last = api.editingProvider.value?.models.at(-1);
    expect(last?.id).toBe('');
  });

  it('含空模型 ID 的草稿保存被拦截且不落盘', async () => {
    const api = setupProviders();
    api.replaceEditing(provider([model('gpt-4o'), model('')]));
    await api.handleSaveCurrentProvider();
    expect(message.warning).toHaveBeenCalledWith('存在未填写模型 ID 的行');
    expect(bridge.upsertProvider).not.toHaveBeenCalled();
  });
});
