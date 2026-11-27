/** 交接期正文单份：Live 容器持有唯一正文，稳态盒 final 槽在换手前隐藏，换手后同一帧接回。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import naive from 'naive-ui';
import { createPinia, setActivePinia } from 'pinia';
import TimelineView from './TimelineView.vue';
import TurnHandoff from './userturn/TurnHandoff.vue';
import { timelineViewFallback, TimelineViewKey, type TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import type { TurnSummary } from '@shared/bridge/hostBridge';

const FINAL_TEXT = '# 末轮正文\n\n这段回答全程只渲染一份。';

const handoffSegs: TimelineLiveSeg[] = [
  { kind: 'reasoning', key: 'r1', text: '思考过程', card: null, tag: 'process' },
  { kind: 'tool', key: 'tool-1', text: '', card: null, tag: 'process' },
  { kind: 'text', key: 'final-1', text: FINAL_TEXT, card: null, tag: 'final' },
];

function summary(): TurnSummary {
  return {
    turnId: 'turn-1',
    turnSeq: 1,
    iterEnd: 1,
    userPreview: '帮我看看',
    finalBlockId: 'bfinal1',
    finalPreview: '这段回答全程只渲染一份。',
    blocks: 3,
    tools: 1,
    failed: 0,
    durationMs: 800,
    chars: 42,
    status: 'completed',
    lastRequestId: 'req',
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
    anchorKey: 'turn-1',
  } as TurnSummary;
}

function mountTimeline() {
  const pinia = createPinia();
  setActivePinia(pinia);
  const ws = useWorkspaceStore(pinia);
  vi.spyOn(ws, 'getBlockContents').mockResolvedValue({
    items: { bfinal1: { blockId: 'bfinal1', kind: 'text', tag: 'final', markdown: FINAL_TEXT, preview: '' } },
    missing: [],
  } as never);

  const stub = {
    ...timelineViewFallback(),
    summaryList: [summary()],
    hasActiveTurn: true,
    handoffActive: true,
    handoffFinalText: FINAL_TEXT,
    handoffReduceHeight: false,
    streamMdKillSwitch: false,
    liveSegsFor: () => handoffSegs as never[],
    renderHtml: (t: string) => t,
    renderStream: (t: string) => t,
  };
  return mount(TimelineView, {
    props: { isEmpty: false, chatWidthMode: 'centered' },
    global: { plugins: [naive, pinia], provide: { [TimelineViewKey as unknown as string]: stub } },
  });
}

describe('交接期正文单份', () => {
  beforeEach(() => {
    window.localStorage.removeItem('halo-turn-handoff');
  });

  it('交接窗内只有 Live 容器中的一份正文，摘要稳态 final 已隐藏', async () => {
    const wrapper = mountTimeline();
    await nextTick();
    expect(wrapper.find('[data-testid="turn-handoff"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="live-row"]').exists()).toBe(false);

    const steadyFinals = wrapper.findAll('[data-testid^="timeline-final-"]');
    expect(steadyFinals.length).toBe(0);
    expect(wrapper.find('[data-testid="handoff-final"] .stream-block').exists()).toBe(true);
    // 全仓 DOM 中 stream-block 只来自交接容器这一份正文。
    expect(wrapper.findAll('.stream-block').length).toBe(wrapper.findAll('[data-testid="handoff-final"] .stream-block').length);
  });

  it('收缩完成后原子换手：撤 Live 容器、稳态正文同帧接回', async () => {
    const wrapper = mountTimeline();
    await nextTick();
    wrapper.findComponent(TurnHandoff).vm.$emit('handoff-done');
    await nextTick();
    await new Promise((r) => setTimeout(r, 0));
    await nextTick();
    expect(wrapper.find('[data-testid="turn-handoff"]').exists()).toBe(false);
    const steadyFinal = wrapper.find('[data-testid="timeline-final-turn-1"]');
    expect(steadyFinal.exists()).toBe(true);
    expect(steadyFinal.find('.stream-block').exists()).toBe(true);
    expect(steadyFinal.text()).toContain('这段回答全程只渲染一份');
  });
});
