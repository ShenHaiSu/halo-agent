// src/Web.Client/src/components/input/ModelSelectorPanel.test.ts
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import ModelSelectorPanel from './ModelSelectorPanel.vue';
import type { ProviderEdit } from '@/components/settings/providerEditing';

function provider(id: string, displayName: string, models: Array<{ id: string; displayName?: string; protocol?: string }>): ProviderEdit {
  return {
    id,
    displayName,
    baseUrl: 'https://x',
    apiKeySet: false,
    newKey: '',
    clearKey: false,
    headersEnabled: true,
    headers: [],
    useProxy: true,
    models: models.map((m) => ({
      id: m.id,
      displayName: m.displayName ?? '',
      protocol: m.protocol ?? 'chat',
      contextWindow: null,
      maxTokens: null,
      acceptsImages: false,
      includeReasoningHistory: true,
    })),
  };
}

describe('ModelSelectorPanel', () => {
  it('按 Provider 分组渲染组头', () => {
    const w = mount(ModelSelectorPanel, {
      props: {
        groups: [provider('a', 'A', [{ id: 'm1' }]), provider('b', 'B', [{ id: 'm2' }])],
        activeProvider: 'a',
        activeModel: 'm1',
      },
    });
    expect(w.text()).toContain('A');
    expect(w.text()).toContain('B');
    expect(w.findAll('[role="group"]').length).toBe(2);
    w.unmount();
  });

  it('同名模型分属两组（靠组头区分，不扁平）', () => {
    const w = mount(ModelSelectorPanel, {
      props: {
        groups: [provider('a', 'A', [{ id: 'same' }]), provider('b', 'B', [{ id: 'same' }])],
        activeProvider: '',
        activeModel: '',
      },
    });
    const options = w.findAll('[role="option"]');
    expect(options.length).toBe(2);
    // 两行都在各自 group 下
    const groups = w.findAll('[role="group"]');
    expect(groups[0]!.text()).toContain('A');
    expect(groups[1]!.text()).toContain('B');
    w.unmount();
  });

  it('搜索过滤大小写不敏感', async () => {
    const w = mount(ModelSelectorPanel, {
      props: {
        groups: [provider('deepseek', 'DeepSeek', [{ id: 'deepseek-chat' }, { id: 'other' }])],
        activeProvider: '',
        activeModel: '',
      },
    });
    const input = w.find('input');
    await input.setValue('DEEPSEEK-CHAT');
    await nextTick();
    expect(w.findAll('[role="option"]').length).toBe(1);
    expect(w.text()).toContain('deepseek-chat');
    w.unmount();
  });

  it('responses 行可选 + 显示 resp 徽标', () => {
    const w = mount(ModelSelectorPanel, {
      props: {
        groups: [provider('a', 'A', [{ id: 'm1' }, { id: 'm2', protocol: 'responses' }])],
        activeProvider: 'a',
        activeModel: 'm1',
      },
    });
    const options = w.findAll('[role="option"]');
    expect(options[0]!.classes()).toContain('current');
    // P13：chat/responses 均放行，只有未知协议才 disabled。
    expect(options[1]!.classes()).not.toContain('disabled');
    expect(options[1]!.attributes('aria-disabled')).toBeUndefined();
    expect(options[1]!.text()).toContain('resp');
    w.unmount();
  });

  it('未知协议行仍 disabled', () => {
    const w = mount(ModelSelectorPanel, {
      props: {
        groups: [provider('a', 'A', [{ id: 'm1', protocol: 'foo' }])],
        activeProvider: '',
        activeModel: '',
      },
    });
    const options = w.findAll('[role="option"]');
    expect(options[0]!.classes()).toContain('disabled');
    expect(options[0]!.attributes('aria-disabled')).toBe('true');
    w.unmount();
  });

  it('Enter 选中 chat 行并 emit select', async () => {
    const w = mount(ModelSelectorPanel, {
      props: {
        groups: [provider('a', 'A', [{ id: 'm1' }, { id: 'm2' }])],
        activeProvider: '',
        activeModel: '',
      },
    });
    await w.find('[role="listbox"]').trigger('keydown', { key: 'Enter' });
    const emitted = w.emitted('select');
    expect(emitted).toBeTruthy();
    expect(emitted![0]).toEqual(['a', 'm1']);
    w.unmount();
  });
});
