// src/Web.Client/src/components/usage/FilterForm.test.ts
// P16：provider 切换清空 model；P17：隐私遮蔽开关默认开、label 假名但 value 真值。
import { beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import FilterForm from './FilterForm.vue';
import { MASK_KEY, maskProviderId } from './providerMask';
import type { ProviderDict } from './usageTypes';

const dict: ProviderDict = {
  openai: {
    display: 'OpenAI',
    models: [{ id: 'm1', display: 'M1' }],
  },
  other: {
    display: 'Other',
    models: [{ id: 'm2', display: 'M2' }],
  },
};

// resolve 读 localStorage：挂载前须与 maskOn 入参同步（页容器 onMaskToggle 同理先写存储再改 prop）。
beforeEach(() => {
  localStorage.removeItem(MASK_KEY);
});
function mountForm(extra: Record<string, unknown> = {}): ReturnType<typeof mount> {
  return mount(FilterForm, {
    props: {
      fromInput: '',
      toInput: '',
      providerId: 'openai',
      modelId: 'm1',
      providerDict: dict,
      activePreset: null,
      querying: false,
      maskOn: true,
      ...extra,
    },
  });
}

describe('FilterForm', () => {
  it('provider 切换清空 modelId（防跨商残留）', async () => {
    const w = mountForm();
    const selects = w.findAllComponents({ name: 'HSelect' });
    // 第一个 HSelect 是 provider
    await selects[0]?.setValue('other');
    const emitted = w.emitted('update:modelId');
    expect(emitted).toBeDefined();
    expect(emitted?.[0]).toEqual(['']);
    expect(w.emitted('update:providerId')?.[0]).toEqual(['other']);
  });

  it('预设按钮点击 emit preset', async () => {
    const w = mountForm({ providerId: '', modelId: '' });
    const btns = w.findAllComponents({ name: 'HButton' });
    // 第一个预设按钮是今日
    await btns[0]?.trigger('click');
    expect(w.emitted('preset')?.[0]).toEqual(['today']);
  });

  it('隐私遮蔽开关默认开', () => {
    const w = mountForm();
    const sw = w.findComponent({ name: 'HSwitch' });
    expect(sw.exists()).toBe(true);
    expect(sw.props('modelValue')).toBe(true);
  });

  it('开时下拉 label 为恒 5 位假名、value 恒真值（遮蔽不污染过滤）', () => {
    const w = mountForm({ providerId: '', modelId: '' });
    const options = w.findAll('option');
    const byValue = new Map(
      options.map((o) => [(o.element as HTMLOptionElement).value, o.text().trim()])
    );
    expect(byValue.get('openai')).toBe(maskProviderId('openai'));
    expect(byValue.get('openai')).toMatch(/^[a-z0-9]{5}$/);
    expect(byValue.get('other')).toBe(maskProviderId('other'));
  });

  it('关时下拉 label 还原真名', () => {
    localStorage.setItem(MASK_KEY, '0'); // 与 maskOn:false 同步（页容器行为）
    const w = mountForm({ providerId: '', modelId: '', maskOn: false });
    const options = w.findAll('option');
    const byValue = new Map(
      options.map((o) => [(o.element as HTMLOptionElement).value, o.text().trim()])
    );
    expect(byValue.get('openai')).toBe('OpenAI');
    expect(byValue.get('other')).toBe('Other');
  });

  it('开关切换 emit update:maskOn（只重绘不重查由页容器保证）', async () => {
    const w = mountForm();
    const sw = w.findComponent({ name: 'HSwitch' });
    await sw.trigger('click');
    expect(w.emitted('update:maskOn')?.[0]).toEqual([false]);
  });
});
