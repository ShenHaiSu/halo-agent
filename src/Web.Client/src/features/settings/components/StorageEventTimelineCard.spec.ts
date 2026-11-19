import { describe, expect, it, vi, beforeEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import naive from 'naive-ui';

const { bridgeStub, messageStub } = vi.hoisted(() => ({
  bridgeStub: {
    getRecentHostEvents: vi.fn(),
  },
  messageStub: {
    success: vi.fn(),
    error: vi.fn(),
  },
}));

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return { ...actual, getHostBridge: () => bridgeStub };
});

vi.mock('naive-ui', async (importOriginal) => {
  const actual = await importOriginal<typeof import('naive-ui')>();
  return { ...actual, useMessage: () => messageStub, useDialog: () => ({ warning: vi.fn() }) };
});

import StorageEventTimelineCard from './StorageEventTimelineCard.vue';
import { useSystemStore } from '@/stores/systemStore';

async function mountCard(pinia: Pinia) {
  const wrapper = mount(StorageEventTimelineCard, { global: { plugins: [naive, pinia] } });
  await flushPromises();
  return wrapper;
}

describe('StorageEventTimelineCard（存储事件时间线）', () => {
  let pinia: Pinia;

  beforeEach(() => {
    pinia = createPinia();
    setActivePinia(pinia);
    useSystemStore().recentHostEvents = [];
    bridgeStub.getRecentHostEvents.mockReset().mockResolvedValue({ ok: true, data: [] });
    messageStub.success.mockReset();
    messageStub.error.mockReset();
  });

  it('无事件时显示空态文案，不渲染时间线', async () => {
    const wrapper = await mountCard(pinia);
    expect(wrapper.find('[data-testid="storage-events-empty"]').exists()).toBe(true);
    expect(wrapper.find('.n-timeline').exists()).toBe(false);
  });

  it('两类内存事件分别映射标题与摘要', async () => {
    useSystemStore().recentHostEvents = [
      { type: 'storage:changed', summary: 'C:\\old -> D:\\new (3 files)', timestamp: 2000 },
      { type: 'storage:migration-progress', summary: '[80%] 复制中', timestamp: 1000 },
    ];
    const wrapper = await mountCard(pinia);
    const text = wrapper.text();
    expect(text).toContain('存储路径变更');
    expect(text).toContain('迁移进度');
    expect(text).toContain('C:\\old -> D:\\new (3 files)');
    expect(text).toContain('[80%] 复制中');
  });

  it('加载历史后合并且按 timestamp+type 去重', async () => {
    useSystemStore().recentHostEvents = [{ type: 'storage:changed', summary: 'C:\\old -> D:\\new (3 files)', timestamp: 2000 }];
    bridgeStub.getRecentHostEvents.mockResolvedValue({
      ok: true,
      data: [
        {
          type: 'storage:changed',
          payload: { oldDataRootPath: 'C:\\old', newDataRootPath: 'D:\\new', migratedFileCount: 3 },
          timestamp: 2000,
        },
        {
          type: 'storage:changed',
          payload: { oldDataRootPath: 'D:\\new', newDataRootPath: 'E:\\next', migratedFileCount: 5 },
          timestamp: 3000,
        },
      ],
    });

    const wrapper = await mountCard(pinia);
    await wrapper.find('[data-testid="load-storage-history-btn"]').trigger('click');
    await flushPromises();

    expect(bridgeStub.getRecentHostEvents).toHaveBeenCalledWith(20);
    const text = wrapper.text();
    expect(text).toContain('E:\\next');
    // 同一 timestamp+type+summary 的内存事件与持久事件只渲染一次。
    expect(text.match(/C:\\old -> D:\\new \(3 files\)/g)?.length).toBe(1);
  });
});
