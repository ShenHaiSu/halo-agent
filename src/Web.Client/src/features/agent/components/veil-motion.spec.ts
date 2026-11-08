/** P03 组件测 ≥3 例（09 §9.1）：is-leaving 类名 / 关开关行为 / reduced-motion 与 token 接线。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import TimelineView from '@features/agent/components/TimelineView.vue';

const TIMELINE_SFC = (): string => readFileSync(resolve(process.cwd(), 'src/features/agent/components/TimelineView.vue'), 'utf8');

function mountTimeline(extra: Record<string, unknown> = {}) {
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
      hasActiveTurn: false,
      thinkingLine: '',
      isGenerating: false,
      segs: [],
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
      ...extra,
    } as never,
    global: { plugins: [naive] },
  });
}

describe('P03 veil-motion 组件接线', () => {
  it('motionOn=true 且 leaving：veil 常驻并挂 is-leaving 类（G2-1）', () => {
    const wrapper = mountTimeline({ veilCovering: true, veilLeaving: true, motionOn: true });
    const veil = wrapper.find('.transcript-veil');
    expect(veil.exists()).toBe(true);
    expect(veil.classes()).toContain('is-leaving');
  });

  it('motionOn=false（关开关）：veil 无过渡类 = 今日硬摘行为（G5-2，允许类名差异）', () => {
    const wrapper = mountTimeline({ veilCovering: true, veilLeaving: false, veilPreEnter: false, motionOn: false });
    const veil = wrapper.find('.transcript-veil');
    expect(veil.exists()).toBe(true);
    expect(veil.classes()).not.toContain('is-leaving');
    expect(veil.classes()).not.toContain('is-pre-enter');
    // 内容侧无 inert（关开关路径不加 inert = 今日行为；aria-busy 纯标注保留）
    expect(wrapper.find('.chat-timeline-centered').attributes('inert')).toBeUndefined();
  });

  it('covering 期内容列挂 inert + aria-busy（开 motion；误触阻挡由 inert 承担）', () => {
    const wrapper = mountTimeline({ veilCovering: true, motionOn: true });
    const inner = wrapper.find('.chat-timeline-centered');
    expect(inner.exists()).toBe(true);
    expect(inner.attributes('aria-busy')).toBe('true');
    expect(inner.attributes('inert')).toBeDefined();
  });

  it('SFC：--veil-leave-ms:180 声明在 scroll 层 + 主题直连全局块（G3-2）', () => {
    const src = TIMELINE_SFC();
    expect(src).toContain('--veil-leave-ms: 180ms');
    expect(src).toContain('html.dark .transcript-veil');
    expect(src).toContain('html.light .transcript-veil');
    expect(src).toContain('html.dark .transcript-skeleton');
    expect(src).toContain('html.light .transcript-skeleton');
    expect(src).toContain('var(--n-body-color, #0d1117)');
    expect(src).toContain('var(--n-body-color, #f8fafc)');
    // 回归门：scoped 内 `:global(html.x) .foo` 会被编译器丢弃后代选择器（暗黑恒白屏），禁止回退该写法
    expect(src).not.toContain(':global(html)');
    // 禁白闪回退：禁止裸 #fff/white 背景
    expect(/background:\s*(#fff|white)\b/.test(src)).toBe(false);
  });

  it('SFC：reduced-motion 覆盖 veil-shimmer + veil 过渡（G5-1）', () => {
    const src = TIMELINE_SFC();
    const media = /@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{([\s\S]*?)\n\}/.exec(src);
    // 至少存在覆盖 veil/skeleton 的 reduced-motion 块
    const blocks = src.match(/prefers-reduced-motion/g) || [];
    expect(blocks.length).toBeGreaterThanOrEqual(2);
    expect(src).toContain('.transcript-skeleton { transition: none; animation: none; }');
    void media;
  });

  it('SFC：veil 滚动透传 none + skeleton 双主题色（G3-1 取色源）', () => {
    const src = TIMELINE_SFC();
    expect(src).toContain('pointer-events: none');
    expect(src).toContain('#0d1117');
    expect(src).toContain('#f8fafc');
    expect(src).toContain('#1b2430');
    expect(src).toContain('#e2e8f0');
  });
});
