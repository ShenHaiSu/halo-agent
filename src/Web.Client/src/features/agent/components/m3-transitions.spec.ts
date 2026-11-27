/** 轮内过渡：Live 行直挂流段 + 交接容器单份正文 + 流式光标。守卫以 DOM 行为为准，不再只正则检查 CSS 文本。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { createPinia, setActivePinia } from 'pinia';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import TimelineView from '@features/agent/components/TimelineView.vue';
import StreamSegItem from '@features/agent/components/StreamSegItem.vue';
import { TimelineViewKey, timelineViewFallback } from '@features/agent/composables/timelineViewKey';

const seg = (key: string, text: string) => ({ kind: 'text', key, text, card: null, tag: 'process' });
const TIMELINE_SFC = (): string => readFileSync(resolve(process.cwd(), 'src/features/agent/components/TimelineView.vue'), 'utf8');

function mountTimeline(liveSegs: unknown[], over: Record<string, unknown> = {}) {
  setActivePinia(createPinia());
  const stub = {
    ...timelineViewFallback(),
    summaryList: [],
    pendingUser: null,
    hasActiveTurn: true,
    liveSegsFor: () => liveSegs as never[],
    renderHtml: (t: string) => t,
    renderStream: (t: string) => t,
    isGenerating: true,
    streamMdKillSwitch: true,
    ...over,
  };
  return mount(TimelineView, {
    props: {
      isEmpty: false,
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
  it('live 段直挂 Live 行，不再经轮容器', () => {
    const wrapper = mountTimeline([seg('s1', '第一段'), seg('s2', '第二段')]);
    expect(wrapper.find('[data-testid="live-row"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="active-turn-container"]').exists()).toBe(false);
  });

  it('行为守卫：交接窗一开，Live 行让位给交接容器，正文由单一 final 槽承担', () => {
    const finalSeg = { ...seg('final-1', '交接正文'), tag: 'final' };
    const wrapper = mountTimeline([seg('r1', '思考'), finalSeg], {
      handoffActive: true,
      handoffReduceHeight: false,
      streamMdKillSwitch: false,
    });
    expect(wrapper.find('[data-testid="turn-handoff"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="live-row"]').exists()).toBe(false);
    const finalSlot = wrapper.find('[data-testid="handoff-final"]');
    expect(finalSlot.exists()).toBe(true);
    expect(finalSlot.text()).toContain('交接正文');
    // 过程段在独立 wrapper 里，不套 .timeline-turn 的 content-visibility（测量才准）。
    const processWrapper = wrapper.find('[data-testid="handoff-process-wrapper"]');
    expect(processWrapper.exists()).toBe(true);
    expect(processWrapper.classes()).not.toContain('timeline-turn');
  });

  it('行为守卫：热降级 off 时退回原 Live 行，不引入交接容器', () => {
    window.localStorage.setItem('halo-turn-handoff', 'off');
    try {
      const wrapper = mountTimeline([seg('s1', '第一段')], { handoffActive: false });
      expect(wrapper.find('[data-testid="live-row"]').exists()).toBe(true);
      expect(wrapper.find('[data-testid="turn-handoff"]').exists()).toBe(false);
    } finally {
      window.localStorage.removeItem('halo-turn-handoff');
    }
  });

  it('轴尾容器与旧开关无残留', () => {
    const src = TIMELINE_SFC();
    expect(src).not.toContain('active-turn-container');
    expect(src).not.toContain('showAxis' + 'Tail');
    expect(src).not.toContain('live' + 'Coalesce');
    // 已删除的 V1 过渡 CSS 不得复活（死代码曾让文本守卫长期假绿）。
    expect(src).not.toContain('.seg-leave-active');
    expect(src).not.toContain('.live-turn-enter-active');
  });

  it('流式光标只在尾段显示', () => {
    expect(cursorOf(true).find('.smooth-cursor').exists()).toBe(true);
    expect(cursorOf(false).find('.smooth-cursor').exists()).toBe(false);
  });
});
