// src/Web.Client/src/components/usage/FilterForm.test.ts
// P16：provider 切换清空 model；全部时间只清日期由 usagePresets 覆盖，此处断言组件行为。
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import FilterForm from './FilterForm.vue';
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

function mountForm(extra: Record<string, unknown> = {}): ReturnType<typeof mount> {
  return mount(FilterForm, {
    props: {
      fromInput: '',
      toInput: '',
      providerId: 'openai',
      modelId: 'm1',
      sessionId: '',
      providerDict: dict,
      activePreset: null,
      querying: false,
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
});
