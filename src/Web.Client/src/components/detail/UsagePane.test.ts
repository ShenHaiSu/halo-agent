// P24：右抽屉网络请求行取 max(DB 落盘, 内存事件) + 三条 hint 文案断言。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';
import UsagePane from './UsagePane.vue';
import { useMessagesStore } from '@/stores/messages';
import { useSessionStore } from '@/stores/session';
import { transport } from '@/transport';

const summaryOf = (llmCalls: number) => ({
  input: 100,
  cached: 10,
  output: 20,
  total: 120,
  requests: 2,
  llmCalls,
  hitRate: 0.1,
  byDay: [],
  byModel: [],
});

async function flush(ms = 20): Promise<void> {
  await new Promise((r) => setTimeout(r, ms));
  await nextTick();
}

function llmCallsRowText(wrapper: ReturnType<typeof mount>): string {
  const rows = wrapper.findAll('.urow');
  const row = rows.find((r) => r.text().includes('网络请求'));
  return row ? row.text().replace(/\s+/g, ' ') : '';
}

describe('UsagePane P24', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  function setup() {
    const pinia = createPinia();
    setActivePinia(pinia);
    return pinia;
  }

  it('DB 落盘值更大时显示 DB 值', async () => {
    const pinia = setup();
    const session = useSessionStore();
    session.applyInfo({ sessionId: 's-1' });
    session.llmCalls = 3;
    vi.spyOn(transport, 'request').mockResolvedValue(summaryOf(10) as never);
    const wrapper = mount(UsagePane, { global: { plugins: [pinia] } });
    await flush();
    expect(llmCallsRowText(wrapper)).toContain('10');
    wrapper.unmount();
  });

  it('内存事件值更新时取 max（盖 DB 滞后一拍）', async () => {
    const pinia = setup();
    const session = useSessionStore();
    session.applyInfo({ sessionId: 's-1' });
    vi.spyOn(transport, 'request').mockResolvedValue(summaryOf(10) as never);
    const wrapper = mount(UsagePane, { global: { plugins: [pinia] } });
    await flush();
    session.llmCalls = 15;
    await nextTick();
    expect(llmCallsRowText(wrapper)).toContain('15');
    wrapper.unmount();
  });

  it('三条 hint 文案存在', async () => {
    const pinia = setup();
    const session = useSessionStore();
    session.applyInfo({ sessionId: 's-1' });
    vi.spyOn(transport, 'request').mockResolvedValue(summaryOf(10) as never);
    // 给一个可见回合，避免走空态分支
    const messages = useMessagesStore();
    messages.turns.push({ turnId: 't-1' } as never);
    const wrapper = mount(UsagePane, { global: { plugins: [pinia] } });
    await flush();
    const text = wrapper.text();
    expect(text).toContain(
      'LLM调用为本会话落盘累计，重启/切回历史会话后仍保留；中断/零用量回合的调用次数不计入。'
    );
    expect(text).toContain('回合数为界面可见回合；用量记录为有用量落盘的回合数');
    expect(text).toContain('水位为最近一次请求的瞬时上下文占用，用量合计为本会话落盘累计');
    wrapper.unmount();
  });
});
