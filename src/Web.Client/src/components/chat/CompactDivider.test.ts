import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import CompactDivider from './CompactDivider.vue';
import type { CompactDividerView } from '@/contract/agent';

describe('CompactDivider 上下文压缩横线栏', () => {
  const sampleDivider: CompactDividerView = {
    seq: 1,
    summary: '测试上下文浓缩摘要内容',
    removedTurns: 3,
    keptTurns: 2,
    tsMs: 1710000000000,
  };

  it('正确渲染横线栏标题与折叠元数据', () => {
    const wrapper = mount(CompactDivider, {
      props: {
        divider: sampleDivider,
      },
      global: {
        stubs: {
          HaloIcon: true,
        },
      },
    });

    expect(wrapper.text()).toContain('第 1 次上下文压缩');
    expect(wrapper.text()).toContain('折叠 3 轮 · 保留 2 轮');
    expect(wrapper.find('.divider-detail-card').exists()).toBe(false);
  });

  it('点击胶囊徽标可原地展开并收起摘要正文', async () => {
    const wrapper = mount(CompactDivider, {
      props: {
        divider: sampleDivider,
      },
      global: {
        stubs: {
          HaloIcon: true,
        },
      },
    });

    // 默认折叠
    expect(wrapper.find('.divider-detail-card').exists()).toBe(false);

    // 点击展开
    await wrapper.find('.divider-badge').trigger('click');
    expect(wrapper.find('.divider-detail-card').exists()).toBe(true);
    expect(wrapper.find('.detail-body').text()).toContain('测试上下文浓缩摘要内容');
    expect(wrapper.find('.detail-title').text()).toContain('此前会话要点浓缩');

    // 再次点击收起
    await wrapper.find('.divider-badge').trigger('click');
    expect(wrapper.find('.divider-detail-card').exists()).toBe(false);
  });
});
