/** 过渡动画：轮内 live 渲染 + 流式光标（§3.1）。轴尾容器已收敛进轮内，断言随之更新。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import TimelineView from '@features/agent/components/TimelineView.vue';
import StreamSegItem from '@features/agent/components/StreamSegItem.vue';
import { TimelineViewKey, timelineViewFallback } from '@features/agent/composables/timelineViewKey';

const seg = (key: string, text: string) => ({ kind: 'text', key, text, card: null });
const TIMELINE_SFC = (): string => readFileSync(resolve(process.cwd(), 'src/features/agent/components/TimelineView.vue'), 'utf8');

function mountTimeline(liveSegs: unknown[]) {
  const turn = {
    key: 'uturn-u1',
    userId: 'u1',
    userMsg: { id: 'u1', role: 'user', content: 'hi', createdAtUtc: '' },
    steps: [],
    stepCount: 0,
    toolCount: 0,
    isRunning: true,
  };
  const item = { key: 'uturn-u1', kind: 'user-turn', turn };
  const stub = {
    ...timelineViewFallback(),
    liveSegsFor: () => liveSegs as never[],
    renderHtml: (t: string) => t,
    renderStream: (t: string) => t,
    isGenerating: true,
    streamMdKillSwitch: true,
  };
  return mount(TimelineView, {
    props: {
      isEmpty: false,
      items: [item] as never[],
      historyItems: [],
      tailItems: [item] as never[],
      chatWidthMode: 'centered',
    },
    global: {
      plugins: [naive],
      provide: { [TimelineViewKey as unknown as string]: stub },
    },
  });
}

function cursorOf(isTail: boolean) {
  return mount(StreamSegItem, {
    props: {
      seg: seg('t1', '正在输出的文本') as never,
      streamMdKillSwitch: true,
      renderStream: (t: string) => t,
      isTail,
      isGenerating: true,
    },
    global: { plugins: [naive] },
  });
}

describe('轮内过渡', () => {
  it('live 段收敛进轮内渲染（user-turn-live），不再有轴尾容器', () => {
    const wrapper = mountTimeline([seg('s1', '第一段'), seg('s2', '第二段')]);
    expect(wrapper.find('[data-testid="user-turn-live"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-seg]')).toHaveLength(2);
    expect(wrapper.find('[data-testid="active-turn-container"]').exists()).toBe(false);
  });

  it('红线：.seg-leave-active 必须 position:absolute（否则「下移再上移」双重抖动）', () => {
    const rule = /\.seg-leave-active\s*\{([^}]*)\}/.exec(TIMELINE_SFC());
    expect(rule).not.toBeNull();
    expect(rule![1]).toContain('position: absolute');
    // 同时确认没有对高度做过渡（聊天流只增不减，height 动画会与锚定/跟随打架）
    expect(rule![1]).not.toContain('height');
  });

  it('轴尾容器与旧开关无残留', () => {
    const src = TIMELINE_SFC();
    expect(src).not.toContain('active-turn-container');
    expect(src).not.toContain('showAxis' + 'Tail');
    expect(src).not.toContain('live' + 'Coalesce');
  });

  it('流式光标只在尾段显示', () => {
    expect(cursorOf(true).find('.smooth-cursor').exists()).toBe(true);
    expect(cursorOf(false).find('.smooth-cursor').exists()).toBe(false);
  });
});
