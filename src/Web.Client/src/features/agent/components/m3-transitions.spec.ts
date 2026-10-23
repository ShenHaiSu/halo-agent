/** M3 过渡动画：TransitionGroup 结构 + leave 脱流红线 + 流式光标（§3.1）。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import TimelineView from '@features/agent/components/TimelineView.vue';
import StreamSegItem from '@features/agent/components/StreamSegItem.vue';

const seg = (key: string, text: string) => ({ kind: 'text', key, text, card: null });
const TIMELINE_SFC = (): string => readFileSync(resolve(process.cwd(), 'src/features/agent/components/TimelineView.vue'), 'utf8');

function mountTimeline(segs: unknown[]) {
  return mount(TimelineView, {
    props: {
      isEmpty: false,
      items: [],
      historyItems: [],
      tailItems: [],
      chatWidthMode: 'centered',
      isExpanded: () => false,
      anchorRef: () => () => undefined,
      msgImages: () => [],
      thumbOf: () => '',
      hasLost: () => false,
      renderHtml: (t: string) => t,
      renderStream: (t: string) => t,
      cardFor: () => ({}) as never,
      toolKey: () => '',
      hasFailed: () => false,
      showTelemetry: () => false,
      protocolOf: () => '',
      cacheLineOf: () => '',
      hasActiveTurn: true,
      thinkingLine: '',
      isGenerating: true,
      segs: segs as never,
      segKillSwitch: false,
      fallbackCards: [],
      streamingText: '',
      showThinking: false,
      turnNotice: '',
      lastError: '',
      showRetry: false,
      liveTelemetry: null,
      menuVisible: false,
      menuX: 0,
      menuY: 0,
      menuBusy: false,
      menuCanCopy: false,
    },
    global: { plugins: [naive] },
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

describe('M3 过渡动画', () => {
  it('seg 列表渲染为 TransitionGroup 容器（.seg-list），子段保持 data-seg', () => {
    const wrapper = mountTimeline([seg('s1', '第一段'), seg('s2', '第二段')]);
    expect(wrapper.find('.seg-list').exists()).toBe(true);
    expect(wrapper.findAll('[data-seg]')).toHaveLength(2);
    expect(wrapper.find('[data-testid="active-turn-container"]').exists()).toBe(true);
  });

  it('红线：.seg-leave-active 必须 position:absolute（否则「下移再上移」双重抖动）', () => {
    const rule = /\.seg-leave-active\s*\{([^}]*)\}/.exec(TIMELINE_SFC());
    expect(rule).not.toBeNull();
    expect(rule![1]).toContain('position: absolute');
    // 同时确认没有对高度做过渡（聊天流只增不减，height 动画会与锚定/跟随打架）
    expect(rule![1]).not.toContain('height');
  });

  it('活动容器使用 Transition，且过渡只动 opacity/transform（无 height/max-height）', () => {
    const src = TIMELINE_SFC();
    expect(src).toContain('<Transition name="live-turn">');
    const rule = /\.live-turn-enter-active\s*\{([^}]*)\}/.exec(src);
    expect(rule).not.toBeNull();
    expect(rule![1]).toContain('opacity');
    expect(rule![1]).not.toContain('max-height');
  });

  it('流式光标只在尾段显示', () => {
    expect(cursorOf(true).find('.smooth-cursor').exists()).toBe(true);
    expect(cursorOf(false).find('.smooth-cursor').exists()).toBe(false);
  });
});
