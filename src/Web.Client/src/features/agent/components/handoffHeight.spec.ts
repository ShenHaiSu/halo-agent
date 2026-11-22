/** 过程段收缩行为：实测 px 起高 → 双 rAF 归零 → 换手；折叠条与收缩同帧出现。 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import TurnHandoff from './userturn/TurnHandoff.vue';
import type { TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';

const segs: TimelineLiveSeg[] = [
  { kind: 'reasoning', key: 'r1', text: '思考', card: null, tag: 'process' },
  { kind: 'text', key: 'f1', text: '正文', card: null, tag: 'final' },
];

let offsetSpy: { mockRestore(): void } | null = null;
let mediaSpy: { mockRestore(): void } | null = null;

function mockHeight(px: number): void {
  offsetSpy = vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(px);
}

afterEach(() => {
  offsetSpy?.mockRestore();
  mediaSpy?.mockRestore();
  offsetSpy = null;
  mediaSpy = null;
  window.localStorage.removeItem('halo-turn-handoff');
});

function mountHandoff(over: Record<string, unknown> = {}) {
  setActivePinia(createPinia());
  return mount(TurnHandoff, {
    props: { segs, completing: true, reduceHeight: true, ...over },
    global: { plugins: [createPinia()] },
  });
}

async function settle(): Promise<void> {
  await new Promise((r) => setTimeout(r, 260));
}

describe('过程段高度收缩', () => {
  it('实测首帧高度写死后归零，折叠条同帧出现，动画结束发换手信号', async () => {
    mockHeight(480);
    const wrapper = mountHandoff();
    await nextTick();
    const process = wrapper.find('[data-testid="handoff-process-wrapper"]').element as HTMLElement;

    // 折叠条与收缩起点同一帧出现，不参与收缩。
    expect(wrapper.find('[data-testid="handoff-collapse-bar"]').exists()).toBe(true);
    expect(process.style.height).toBe('480px');
    expect(process.style.overflow).toBe('hidden');
    await settle();

    // 高度单调收至 0；单帧落差不会再出现「整块 Live 体瞬间消失」。
    expect(process.style.height).toBe('0px');
    expect(process.style.transition).toContain('height 180ms');
    expect(wrapper.emitted('handoff-done')).toHaveLength(1);
  });

  it('prefers-reduced-motion 下直通为 0ms，仍不双份且不发墙钟定时器', async () => {
    mediaSpy = vi.spyOn(window, 'matchMedia').mockReturnValue({ matches: true } as MediaQueryList);
    mockHeight(480);
    const wrapper = mountHandoff();
    const process = wrapper.find('[data-testid="handoff-process-wrapper"]').element as HTMLElement;
    expect(process.style.height).toBe('0px');
    expect(process.style.transition).toBe('');
    expect(wrapper.emitted('handoff-done')).toHaveLength(1);
  });

  it('关闭收缩时容器只做单份编排：不写高度、不出折叠条、不发换手（回到硬切路径）', async () => {
    mockHeight(480);
    const wrapper = mountHandoff({ reduceHeight: false });
    const process = wrapper.find('[data-testid="handoff-process-wrapper"]').element as HTMLElement;
    expect(wrapper.find('[data-testid="handoff-collapse-bar"]').exists()).toBe(false);
    expect(process.style.height).toBe('');
    await settle();
    expect(wrapper.emitted('handoff-done')).toBeUndefined();
    // 正文仍只有 Live 一份
    expect(wrapper.find('[data-testid="handoff-final"] .stream-block').exists()).toBe(true);
  });
});
