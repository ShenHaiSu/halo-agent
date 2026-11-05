import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import MessageItem from './MessageItem.vue';
import type { MessageItem as Msg } from '@/stores/messages';

// MessageItem 依赖 transport/session 等模块：只 mock 必要网络调用
vi.mock('@/transport', () => ({
  transport: { request: vi.fn() },
}));

function msg(overrides: Partial<Msg> = {}): Msg {
  return {
    id: 'm1',
    role: 'assistant',
    content: '正文',
    ts: Date.now(),
    ...overrides,
  } as Msg;
}

beforeEach(() => {
  setActivePinia(createPinia());
});

describe('MessageItem 中断文案优先级', () => {
  it('F4: 两者同时 true 时只显示「已由你中断。」', () => {
    const w = mount(MessageItem, {
      props: {
        message: msg({ truncatedByLength: true, truncatedByUser: true }),
        isStreaming: false,
      },
    });
    const warns = w.findAll('.msg-warn');
    expect(warns).toHaveLength(1);
    expect(warns[0]!.text()).toContain('已由你中断');
  });

  it('仅 truncatedByLength 显示超长截断', () => {
    const w = mount(MessageItem, {
      props: {
        message: msg({ truncatedByLength: true }),
        isStreaming: false,
      },
    });
    expect(w.find('.msg-warn').text()).toContain('最大长度');
  });
});
