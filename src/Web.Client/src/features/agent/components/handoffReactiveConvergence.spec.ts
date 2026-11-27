/** 响应式交接集成：换手前后正文可见实例恒为 1，种子撤销后收敛到落盘正文。 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick, reactive } from 'vue';
import naive from 'naive-ui';
import { createPinia, setActivePinia } from 'pinia';
import TimelineView from './TimelineView.vue';
import TurnHandoff from './userturn/TurnHandoff.vue';
import { timelineViewFallback, TimelineViewKey, type TimelineLiveSeg } from '@features/agent/composables/timelineViewKey';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import type { TurnSummary } from '@shared/bridge/hostBridge';

const TURN_ID = 'turn-reactive';
const SEED = '污染种子 LIVE_MARK';
const CLEAN = '落盘正文 CLEAN_MARKER_OK';

const segs: TimelineLiveSeg[] = [
  { kind: 'reasoning', key: 'rsn-a', text: '思考过程', card: null, tag: 'process' },
  { kind: 'text', key: 'txt-final', text: '交接期唯一正文', card: null, tag: 'final' },
];

function summary(): TurnSummary {
  return {
    turnId: TURN_ID,
    turnSeq: 1,
    iterEnd: 1,
    userPreview: '帮我看看',
    finalBlockId: 'blk-final',
    finalPreview: '落盘预览',
    blocks: 2,
    tools: 0,
    failed: 0,
    durationMs: 800,
    chars: 40,
    status: 'completed',
    lastRequestId: 'req-reactive',
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
    anchorKey: TURN_ID,
  } as TurnSummary;
}

async function flush(): Promise<void> {
  await nextTick();
  await new Promise((r) => setTimeout(r, 0));
  await nextTick();
}

function mountTimeline(seed: string) {
  const pinia = createPinia();
  setActivePinia(pinia);
  const ws = useWorkspaceStore(pinia);
  const getBlockContents = vi.spyOn(ws, 'getBlockContents').mockResolvedValue({
    items: { 'blk-final': { blockId: 'blk-final', kind: 'text', tag: 'final', markdown: CLEAN, preview: '' } },
    missing: [],
  } as never);
  const vm = reactive({
    ...timelineViewFallback(),
    summaryList: [summary()],
    hasActiveTurn: true,
    handoffActive: true,
    handoffFinalText: seed,
    handoffReduceHeight: false,
    streamMdKillSwitch: false,
    liveSegsFor: () => segs,
    renderHtml: (t: string) => t,
    renderStream: (t: string) => t,
  });
  const wrapper = mount(TimelineView, {
    props: { isEmpty: false, chatWidthMode: 'centered' },
    global: { plugins: [naive, pinia], provide: { [TimelineViewKey as unknown as string]: vm } },
  });
  return { wrapper, vm, getBlockContents };
}

beforeEach(() => {
  window.localStorage.removeItem('halo-turn-handoff');
});

describe('响应式交接收敛', () => {
  it('交接窗内只有 Live 一份正文，稳态 final 槽隐藏', async () => {
    const { wrapper } = mountTimeline(SEED);
    await flush();
    expect(wrapper.find('[data-testid="turn-handoff"]').exists()).toBe(true);
    expect(wrapper.findAll('[data-testid^="timeline-final-"]').length).toBe(0);
    expect(wrapper.findAll('.stream-block').length).toBe(1);
    expect(wrapper.find('[data-testid="handoff-final"] .stream-block').exists()).toBe(true);
  });

  it('handoff-done 后同帧撤 Live、显稳态，无正文真空', async () => {
    const { wrapper } = mountTimeline(SEED);
    await flush();
    wrapper.findComponent(TurnHandoff).vm.$emit('handoff-done');
    await nextTick();
    expect(wrapper.find('[data-testid="turn-handoff"]').exists()).toBe(false);
    const steady = wrapper.find(`[data-testid="timeline-final-${TURN_ID}"]`);
    expect(steady.exists()).toBe(true);
    expect(steady.find('.stream-block').exists()).toBe(true);
    expect(steady.text()).toContain('LIVE_MARK');
    expect(wrapper.findAll('.stream-block').length).toBe(1);
  });

  it('handoff-done 同帧撤销种子并回读落盘，污染值不再进入稳态', async () => {
    const { wrapper, vm, getBlockContents } = mountTimeline(SEED);
    await flush();
    wrapper.findComponent(TurnHandoff).vm.$emit('handoff-done');
    await nextTick();
    // 回读在途时仍保留种子正文，不闪加载态；回包到达后同帧替换为落盘正文。
    expect(wrapper.find(`[data-testid="timeline-final-loading-${TURN_ID}"]`).exists()).toBe(false);
    await flush();

    expect(getBlockContents).toHaveBeenCalledWith(['blk-final']);
    const steady = wrapper.find(`[data-testid="timeline-final-${TURN_ID}"]`);
    expect(steady.text()).toContain('CLEAN_MARKER_OK');
    expect(steady.text()).not.toContain('LIVE_MARK');
    expect(wrapper.findAll('.stream-block').length).toBe(1);

    vm.handoffActive = false;
    await flush();
    expect(wrapper.find(`[data-testid="timeline-final-${TURN_ID}"]`).text()).toContain('CLEAN_MARKER_OK');
  });

  it('空种子路径直接读落盘正文，不出现加载态或预览二次跳变', async () => {
    const { wrapper, vm } = mountTimeline('');
    await flush();
    expect(wrapper.find(`[data-testid="timeline-final-loading-${TURN_ID}"]`).exists()).toBe(false);
    vm.handoffActive = false;
    await flush();
    const steady = wrapper.find(`[data-testid="timeline-final-${TURN_ID}"]`);
    expect(steady.text()).toContain('CLEAN_MARKER_OK');
    expect(steady.text()).not.toContain('落盘预览');
  });
});
